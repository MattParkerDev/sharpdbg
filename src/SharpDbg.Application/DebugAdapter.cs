using SharpDbg.Infrastructure.Debugger;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
// Newtonsoft.Json.Linq is required for accessing ConfigurationProperties from LaunchArguments/AttachArguments
// The Microsoft DAP library uses JToken for dynamic configuration properties
using Newtonsoft.Json.Linq;
using MSBreakpoint = Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages.Breakpoint;
using MSThread = Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages.Thread;
using MSStackFrame = Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages.StackFrame;

namespace SharpDbg.Application;

/// <summary>
/// Main debug adapter that coordinates DAP protocol and debugger engine
/// </summary>
public class DebugAdapter : DebugAdapterBase
{
    private readonly ManagedDebugger _debugger;
    private readonly Action<string>? _logger;
    private bool _clientLinesStartAt1 = true;
    private bool _clientColumnsStartAt1 = true;

    public DebugAdapter(Action<string>? logger = null)
    {
        _logger = logger;
        _debugger = new ManagedDebugger(logger);

        // Subscribe to debugger events
        SubscribeToDebuggerEvents();
    }

    public void Initialize(Stream input, Stream output)
    {
        InitializeProtocolClient(input, output);
    }

    protected static T ExecuteWithExceptionHandling<T>(Func<T> func)
    {
	    try
	    {
		    return func();
	    }
	    catch (ProtocolException)
	    {
		    throw;
	    }
	    catch (Exception ex)
	    {
		    throw new ProtocolException(ex.Message, ex);
	    }
    }

    // Helper method to extract configuration properties from LaunchArguments/AttachArguments
    private static T? GetConfigValue<T>(Dictionary<string, JToken>? config, string key)
    {
        if (config != null && config.TryGetValue(key, out var token))
        {
            return token.ToObject<T>();
        }
        return default;
    }

    private void SubscribeToDebuggerEvents()
    {
        _debugger.OnStopped += (threadId, reason) =>
        {
            Protocol.SendEvent(new StoppedEvent
            {
                Reason = ConvertStopReason(reason),
                ThreadId = threadId,
                AllThreadsStopped = true
            });
        };

        _debugger.OnStopped2 += (threadId, filePath, line, reason) =>
        {
	        var source = new Source { Path = filePath };
	        var stoppedEvent = new StoppedEvent
	        {
		        Reason = ConvertStopReason(reason),
		        ThreadId = threadId,
		        AllThreadsStopped = true
	        };
	        stoppedEvent.AdditionalProperties["source"] = JToken.FromObject(source);
	        stoppedEvent.AdditionalProperties["line"] = JToken.FromObject(line);
	        Protocol.SendEvent(stoppedEvent);
        };

  //       _debugger.OnBreakpointChanged += (breakpoint) =>
		// {
		// 	Protocol.SendEvent(new BreakpointEvent
		// 	{
		// 		Reason = BreakpointEvent.ReasonValue.Changed,
		// 		Breakpoint = new MSBreakpoint
		// 		{
		// 			Id = breakpoint.Id,
		// 			Verified = breakpoint.Verified,
		// 			Line = ConvertDebuggerLineToClient(breakpoint.Line),
		// 			Message = breakpoint.Message,
		// 			Source = new Source
		// 			{
		// 				Path = breakpoint.FilePath
		// 			}
		// 		}
		// 	});
		// };

        _debugger.OnContinued += (threadId) =>
        {
            Protocol.SendEvent(new ContinuedEvent
            {
                ThreadId = threadId,
                AllThreadsContinued = true
            });
        };

        _debugger.OnExited += () =>
        {
            Protocol.SendEvent(new ExitedEvent
            {
                ExitCode = 0 // There is no built-in, cross-platform way to get the exit code of an exited process
            });
        };

        _debugger.OnTerminated += () =>
        {
            Protocol.SendEvent(new TerminatedEvent());
        };

        _debugger.OnThreadStarted += (threadId, name) =>
        {
            Protocol.SendEvent(new ThreadEvent
            {
                Reason = ThreadEvent.ReasonValue.Started,
                ThreadId = threadId
            });
        };

        _debugger.OnThreadExited += (threadId, name) =>
        {
            Protocol.SendEvent(new ThreadEvent
            {
                Reason = ThreadEvent.ReasonValue.Exited,
                ThreadId = threadId
            });
        };

        _debugger.OnModuleLoaded += (id, name, path) =>
        {
            Protocol.SendEvent(new ModuleEvent
            {
                Reason = ModuleEvent.ReasonValue.New,
                Module = new Module
                {
                    Id = id,
                    Name = name,
                    Path = path
                }
            });
        };

        _debugger.OnOutput += (output) =>
        {
            Protocol.SendEvent(new OutputEvent
            {
                Category = OutputEvent.CategoryValue.Stdout,
                Output = output
            });
        };
    }

    // Command handlers
    protected override InitializeResponse HandleInitializeRequest(InitializeArguments arguments)
    {
        _clientLinesStartAt1 = arguments.LinesStartAt1 ?? true;
        _clientColumnsStartAt1 = arguments.ColumnsStartAt1 ?? true;

        // Send initialized event
        Protocol.SendEvent(new InitializedEvent());

        return new InitializeResponse
        {
            SupportsConfigurationDoneRequest = true,
            SupportsFunctionBreakpoints = true,
            SupportsConditionalBreakpoints = false,
            SupportsEvaluateForHovers = true,
            SupportsStepBack = false,
            SupportsSetVariable = false,
            SupportsRestartFrame = false,
            SupportsTerminateRequest = true,
            ExceptionBreakpointFilters = new List<ExceptionBreakpointsFilter>
            {
                new() { Filter = "all", Label = "All Exceptions", Default = false },
                new() { Filter = "user-unhandled", Label = "User-Unhandled Exceptions", Default = true }
            }
        };
    }

    protected override LaunchResponse HandleLaunchRequest(LaunchArguments arguments)
    {
        var program = GetConfigValue<string>(arguments.ConfigurationProperties, "program");
        if (string.IsNullOrEmpty(program))
        {
            throw new ProtocolException("Missing program path");
        }

        var args = GetConfigValue<string[]>(arguments.ConfigurationProperties, "args") ?? Array.Empty<string>();
        var cwd = GetConfigValue<string>(arguments.ConfigurationProperties, "cwd");
        var env = GetConfigValue<Dictionary<string, string>>(arguments.ConfigurationProperties, "env");
        var stopAtEntry = GetConfigValue<bool?>(arguments.ConfigurationProperties, "stopAtEntry") ?? false;

        try
        {
            _debugger.Launch(program, args, cwd, env, stopAtEntry);
            return new LaunchResponse();
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"Launch failed: {ex.Message}");
            throw new ProtocolException($"Failed to launch: {ex.Message}");
        }
    }

    protected override AttachResponse HandleAttachRequest(AttachArguments arguments)
    {
        var processId = GetConfigValue<int?>(arguments.ConfigurationProperties, "processId");
        if (processId == null)
        {
            throw new ProtocolException("Missing process ID");
        }

        try
        {
            _debugger.Attach(processId.Value);
            return new AttachResponse();
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"Attach failed: {ex.Message}");
            throw new ProtocolException($"Failed to attach: {ex.Message}");
        }
    }

    protected override ConfigurationDoneResponse HandleConfigurationDoneRequest(ConfigurationDoneArguments arguments)
    {
	    return ExecuteWithExceptionHandling(() =>
	    {
		    _logger?.Invoke("Configuration done");
		    _debugger.ConfigurationDone();
		    return new ConfigurationDoneResponse();
	    });
    }

    protected override SetBreakpointsResponse HandleSetBreakpointsRequest(SetBreakpointsArguments arguments)
    {
	    return ExecuteWithExceptionHandling(() =>
	    {
		    if (arguments.Source?.Path == null)
		    {
			    throw new ProtocolException("Missing source path");
		    }

		    var lines = arguments.Breakpoints?.Select(bp => ConvertClientLineToDebugger(bp.Line)).ToArray() ?? Array.Empty<int>();
		    var breakpoints = _debugger.SetBreakpoints(arguments.Source.Path, lines);

		    var responseBreakpoints = breakpoints.Select(bp => new MSBreakpoint
		    {
			    Id = bp.Id,
			    Verified = bp.Verified,
			    Line = ConvertDebuggerLineToClient(bp.Line),
			    Message = bp.Message,
			    Source = new Source
			    {
				    Path = bp.FilePath
			    }
		    }).ToList();

		    return new SetBreakpointsResponse
		    {
			    Breakpoints = responseBreakpoints
		    };
	    });
    }

    protected override SetFunctionBreakpointsResponse HandleSetFunctionBreakpointsRequest(SetFunctionBreakpointsArguments arguments)
    {
        // Function breakpoints not yet fully implemented
        return new SetFunctionBreakpointsResponse
        {
            Breakpoints = new List<MSBreakpoint>()
        };
    }

    protected override SetExceptionBreakpointsResponse HandleSetExceptionBreakpointsRequest(SetExceptionBreakpointsArguments arguments)
    {
        // Exception breakpoints configuration
        _logger?.Invoke($"Exception breakpoints: {string.Join(", ", arguments?.Filters ?? new List<string>())}");

        return new SetExceptionBreakpointsResponse();
    }

    protected override ThreadsResponse HandleThreadsRequest(ThreadsArguments arguments)
    {
	    return ExecuteWithExceptionHandling(() =>
	    {
		    var threads = _debugger.GetThreads();

		    var responseThreads = threads.Select(t => new MSThread
		    {
			    Id = t.id,
			    Name = t.name
		    }).ToList();

		    return new ThreadsResponse
		    {
			    Threads = responseThreads
		    };
	    });
    }

    protected override StackTraceResponse HandleStackTraceRequest(StackTraceArguments arguments)
    {
	    return ExecuteWithExceptionHandling(() =>
	    {
		    var frames = _debugger.GetStackTrace(arguments.ThreadId, arguments.StartFrame ?? 0, arguments.Levels);

		    var responseFrames = frames.Select(f => new MSStackFrame
		    {
			    Id = f.Id,
			    Name = f.Name,
			    Line = ConvertDebuggerLineToClient(f.Line),
			    EndLine = ConvertDebuggerLineToClient(f.EndLine),
			    Column = ConvertDebuggerColumnToClient(f.Column),
			    EndColumn =  ConvertDebuggerColumnToClient(f.EndColumn),
			    Source = f.Source != null ? new Source { Path = f.Source } : null
		    }).ToList();

		    return new StackTraceResponse
		    {
			    StackFrames = responseFrames,
			    TotalFrames = responseFrames.Count
		    };
	    });
    }

    protected override ScopesResponse HandleScopesRequest(ScopesArguments arguments)
    {
	    return ExecuteWithExceptionHandling(() =>
	    {
		    var scopes = _debugger.GetScopes(arguments.FrameId);

		    var responseScopes = scopes.Select(s => new Scope
		    {
			    Name = s.Name,
			    VariablesReference = s.VariablesReference,
			    Expensive = s.Expensive
		    }).ToList();

		    return new ScopesResponse
		    {
			    Scopes = responseScopes
		    };
	    });
    }

    protected override async void HandleVariablesRequestAsync(IRequestResponder<VariablesArguments, VariablesResponse> responder)
    {
	    try
	    {
		    var variables = await _debugger.GetVariables(responder.Arguments.VariablesReference);

		    var responseVariables = variables.Select(v => new Variable
		    {
			    Name = v.Name,
			    EvaluateName = v.Name,
			    Value = v.Value,
			    Type = v.Type,
			    PresentationHint = v.PresentationHint?.ToDto(),
			    VariablesReference = v.VariablesReference
		    }).ToList();

		    var response = new VariablesResponse
		    {
			    Variables = responseVariables
		    };
		    responder.SetResponse(response);
	    }
	    catch (Exception ex)
	    {
		    _logger?.Invoke($"HandleVariablesRequestAsync failed: {ex.Message} , {ex}");
		    responder.SetError(new ProtocolException($"Failed to get variables: {ex.Message}", ex));
	    }
    }

    protected override async void HandleEvaluateRequestAsync(IRequestResponder<EvaluateArguments, EvaluateResponse> responder)
    {
	    try
	    {
		    var arguments = responder.Arguments;
		    var (result, type, variablesReference) = await _debugger.Evaluate(arguments.Expression, arguments.FrameId);

		    var response = new EvaluateResponse
		    {
			    Result = result,
			    Type = type,
			    VariablesReference = variablesReference
		    };
		    responder.SetResponse(response);
	    }
	    catch (Exception ex)
	    {
		    _logger?.Invoke($"HandleVariablesRequestAsync failed: {ex.Message} , {ex}");
		    responder.SetError(new ProtocolException($"Failed to get variables: {ex.Message}", ex));
	    }
    }

    protected override ContinueResponse HandleContinueRequest(ContinueArguments arguments)
    {
	    return ExecuteWithExceptionHandling(() =>
	    {
		    _debugger.Continue();
		    return new ContinueResponse
		    {
			    AllThreadsContinued = true
		    };
	    });
    }

    protected override NextResponse HandleNextRequest(NextArguments arguments)
    {
	    return ExecuteWithExceptionHandling(() =>
	    {
		    _debugger.StepNext(arguments.ThreadId);
		    return new NextResponse();
	    });
    }

    protected override StepInResponse HandleStepInRequest(StepInArguments arguments)
    {
	    return ExecuteWithExceptionHandling(() =>
	    {
		    _debugger.StepIn(arguments.ThreadId);
		    return new StepInResponse();
	    });
    }

    protected override StepOutResponse HandleStepOutRequest(StepOutArguments arguments)
    {
	    return ExecuteWithExceptionHandling(() =>
	    {
		    _debugger.StepOut(arguments.ThreadId);
		    return new StepOutResponse();
	    });
    }

    protected override PauseResponse HandlePauseRequest(PauseArguments arguments)
    {
	    return ExecuteWithExceptionHandling(() =>
	    {
		    _debugger.Pause();
		    return new PauseResponse();
	    });
    }

    protected override DisconnectResponse HandleDisconnectRequest(DisconnectArguments arguments)
    {
	    return ExecuteWithExceptionHandling(() =>
	    {
		    _debugger.Disconnect(arguments?.TerminateDebuggee ?? false);
		    return new DisconnectResponse();
	    });
    }

    protected override TerminateResponse HandleTerminateRequest(TerminateArguments arguments)
    {
	    return ExecuteWithExceptionHandling(() =>
	    {
		    _debugger.Terminate();
		    return new TerminateResponse();
	    });
    }

    // Coordinate conversion helpers
    private int ConvertClientLineToDebugger(int line)
    {
        return _clientLinesStartAt1 ? line : line + 1;
    }

    private int ConvertDebuggerLineToClient(int line)
    {
        return _clientLinesStartAt1 ? line : line - 1;
    }

    private int ConvertClientColumnToDebugger(int column)
    {
        return _clientColumnsStartAt1 ? column : column + 1;
    }

    private int ConvertDebuggerColumnToClient(int column)
    {
        return _clientColumnsStartAt1 ? column : column - 1;
    }

    private static StoppedEvent.ReasonValue ConvertStopReason(string reason)
    {
        return reason.ToLowerInvariant() switch
        {
            "step" => StoppedEvent.ReasonValue.Step,
            "breakpoint" => StoppedEvent.ReasonValue.Breakpoint,
            "exception" => StoppedEvent.ReasonValue.Exception,
            "pause" => StoppedEvent.ReasonValue.Pause,
            "entry" => StoppedEvent.ReasonValue.Entry,
            "goto" => StoppedEvent.ReasonValue.Goto,
            "function breakpoint" => StoppedEvent.ReasonValue.FunctionBreakpoint,
            "data breakpoint" => StoppedEvent.ReasonValue.DataBreakpoint,
            "instruction breakpoint" => StoppedEvent.ReasonValue.InstructionBreakpoint,
            _ => StoppedEvent.ReasonValue.Unknown
        };
    }
}
