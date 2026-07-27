using Ardalis.GuardClauses;
using SharpDbg.Infrastructure.Debugger;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
// Newtonsoft.Json.Linq is required for accessing ConfigurationProperties from LaunchArguments/AttachArguments
// The Microsoft DAP library uses JToken for dynamic configuration properties
using Newtonsoft.Json.Linq;
using SharpDbg.Infrastructure.Debugger.Models;
using SharpDbg.Infrastructure.Debugger.Models.Response;
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

	private static T ExecuteWithExceptionHandling<T>(Func<T> func)
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
		if (config?.TryGetValue(key, out var token) is true)
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

		_debugger.OnStopped2 += (threadId, filePath, line, column, reason, decompiledSourceInfo) =>
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
			stoppedEvent.AdditionalProperties["column"] = JToken.FromObject(column);
			stoppedEvent.AdditionalProperties["decompiledSourceInfo"] = decompiledSourceInfo is null ? null : JToken.FromObject(decompiledSourceInfo);
			Protocol.SendEvent(stoppedEvent);
		};

		_debugger.OnBreakpointChanged += breakpoint =>
		{
			Protocol.SendEvent(new BreakpointEvent
			{
				Reason = BreakpointEvent.ReasonValue.Changed,
				Breakpoint = new MSBreakpoint
				{
					Id = breakpoint.Id,
					Verified = breakpoint.Verified,
					Line = breakpoint.IsFunctionBreakpoint ? null : ConvertDebuggerLineToClient(breakpoint.Line),
					Column = breakpoint is { IsFunctionBreakpoint: false, Verified: true, Column: not null } ? ConvertDebuggerColumnToClient(breakpoint.Column.Value) : null,
					EndLine = breakpoint is { IsFunctionBreakpoint: false, Verified: true } ? breakpoint.EndLine : null,
					EndColumn = breakpoint is { IsFunctionBreakpoint: false, Verified: true, EndColumn: not null } ? ConvertDebuggerColumnToClient(breakpoint.EndColumn.Value) : null,
					Offset = breakpoint is { IsFunctionBreakpoint: false, Verified: true } ? 0 : null,
					Message = breakpoint.Message,
					Source = breakpoint is not { IsFunctionBreakpoint: false, Verified: true } ? null : new Source
					{
						Path = breakpoint.FilePath,
						Name = Path.GetFileName(breakpoint.FilePath),
						SourceReference = 0
					}
				}
			});
		};

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

		_debugger.OnOutput += (output, isStdError) =>
		{
			Protocol.SendEvent(new OutputEvent
			{
				Category = isStdError ? OutputEvent.CategoryValue.Stderr : OutputEvent.CategoryValue.Stdout,
				Output = output
			});
		};
		_debugger.SendRunInTerminalRequest += launchInfo =>
		{
			var runInTerminalRequest = new RunInTerminalRequest
			{
				Kind = launchInfo.LaunchRequestConsoleType switch
				{
					LaunchRequestConsoleType.IntegratedTerminal => RunInTerminalArguments.KindValue.Integrated,
					LaunchRequestConsoleType.ExternalTerminal => RunInTerminalArguments.KindValue.External,
					_ => throw new ArgumentOutOfRangeException(nameof(launchInfo.LaunchRequestConsoleType), $"Invalid LaunchRequestConsoleType for RunInTerminalRequest: '{launchInfo.LaunchRequestConsoleType}'")
				},
				Arguments = ["dotnet", launchInfo.Program, ..launchInfo.Arguments],
				Cwd = launchInfo.Cwd,
				Env = launchInfo.Env.ToDictionary(kvp => kvp.Key, kvp => (object)kvp.Value),
				Title = $"{Path.GetFileName(launchInfo.Program)} [DEBUG]"
			};
			runInTerminalRequest.Env["DOTNET_DefaultDiagnosticPortSuspend"] = "1";
			var resp = Protocol.SendClientRequestSync(runInTerminalRequest);
			// https://github.com/microsoft/vscode/issues/61640 - ProcessId will not be returned for integratedTerminal or externalTerminal
			// ShellProcessId will be returned for integratedTerminal but not externalTerminal
			if (resp.ProcessId is null) throw new InvalidOperationException("RunInTerminalRequest did not return a process ID. VSCode does not return a process ID for integratedTerminal or externalTerminal. Use internalConsole instead, or use a compliant DAP client. See: https://github.com/microsoft/vscode/issues/61640");
			return resp.ProcessId.Value;
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
			SupportsConditionalBreakpoints = true,
			SupportsHitConditionalBreakpoints = true,
			SupportsEvaluateForHovers = true,
			SupportsStepBack = false,
			SupportsSetVariable = false,
			SupportsRestartFrame = false,
			SupportsTerminateRequest = true,
			SupportsExceptionInfoRequest = true,
			SupportsStepInTargetsRequest = false,
			SupportsGotoTargetsRequest = false,
			ExceptionBreakpointFilters =
			[
				new ExceptionBreakpointsFilter { Filter = "all", Label = "All Exceptions", Default = false },
				new ExceptionBreakpointsFilter { Filter = "user-unhandled", Label = "User-Unhandled Exceptions", Default = true }
			]
		};
	}

	protected override LaunchResponse HandleLaunchRequest(LaunchArguments arguments)
	{
		return ExecuteWithExceptionHandling(() =>
		{
			var program = GetConfigValue<string>(arguments.ConfigurationProperties, "program");
			if (string.IsNullOrEmpty(program))
			{
				throw new ProtocolException("Missing program path");
			}

			var args = GetConfigValue<List<string>>(arguments.ConfigurationProperties, "args") ?? [];
			var cwd = GetConfigValue<string>(arguments.ConfigurationProperties, "cwd");
			var env = GetConfigValue<Dictionary<string, string>>(arguments.ConfigurationProperties, "env") ?? [];
			var stopAtEntry = GetConfigValue<bool?>(arguments.ConfigurationProperties, "stopAtEntry") ?? false;
			var justMyCode = GetConfigValue<bool?>(arguments.ConfigurationProperties, "justMyCode") ?? true;
			var console = GetConfigValue<string>(arguments.ConfigurationProperties, "console");
			var launchRequestConsoleType = console switch
			{
				"integratedTerminal" => LaunchRequestConsoleType.IntegratedTerminal,
				"externalTerminal" => LaunchRequestConsoleType.ExternalTerminal,
				"internalConsole" => LaunchRequestConsoleType.InternalConsole,
				null => LaunchRequestConsoleType.InternalConsole, // Default to internalConsole if not specified
				_ => throw new ArgumentOutOfRangeException(nameof(console), $"Invalid console type: '{console}'")
			};
			var launchInfo = new LaunchInfo
			{
				Program = program,
				Arguments = args,
				Cwd = cwd,
				Env = env,
				StopAtEntry = stopAtEntry,
				LaunchRequestConsoleType = launchRequestConsoleType
			};

			try
			{
				_debugger.Launch(launchInfo, justMyCode);
				return new LaunchResponse();
			}
			catch (Exception ex)
			{
				_logger?.Invoke($"Launch failed: {ex.Message}");
				throw new ProtocolException($"Failed to launch: {ex.Message}");
			}
		});
	}

	protected override AttachResponse HandleAttachRequest(AttachArguments arguments)
	{
		return ExecuteWithExceptionHandling(() =>
		{
			RemoteAttachInfo? remoteAttachInfo = null;
			var processId = GetConfigValue<int?>(arguments.ConfigurationProperties, "processId");
			if (processId is null)
			{
				throw new ProtocolException("Missing process ID");
			}
			var coreClrMobileDebuggerOptions = GetConfigValue<CoreClrMobileDebuggerOptions>(arguments.ConfigurationProperties, "coreClrMobileDebuggerOptions");
			if (coreClrMobileDebuggerOptions is not null)
			{
				Guard.Against.NullOrWhiteSpace(coreClrMobileDebuggerOptions.Address);
				Guard.Against.Null(coreClrMobileDebuggerOptions.Port);
				Guard.Against.NullOrWhiteSpace(coreClrMobileDebuggerOptions.Platform);
				Guard.Against.Null(coreClrMobileDebuggerOptions.IsServer);
				Guard.Against.NullOrWhiteSpace(coreClrMobileDebuggerOptions.MscordbiPath);
				Guard.Against.NullOrWhiteSpace(coreClrMobileDebuggerOptions.AssembliesPath);
				if (File.Exists(coreClrMobileDebuggerOptions.MscordbiPath) is false) throw new ProtocolException($"coreClrMobileDebuggerOptions.mscordbiPath does not exist: {coreClrMobileDebuggerOptions.MscordbiPath}");
				if (Directory.Exists(coreClrMobileDebuggerOptions.AssembliesPath) is false) throw new ProtocolException($"coreClrMobileDebuggerOptions.assembliesPath does not exist: {coreClrMobileDebuggerOptions.AssembliesPath}");
				remoteAttachInfo = new RemoteAttachInfo
				{
					Address = coreClrMobileDebuggerOptions.Address,
					Port = coreClrMobileDebuggerOptions.Port,
					Platform = coreClrMobileDebuggerOptions.Platform,
					IsServer = coreClrMobileDebuggerOptions.IsServer,
					MscordbiPath = coreClrMobileDebuggerOptions.MscordbiPath,
					AssembliesPath = coreClrMobileDebuggerOptions.AssembliesPath
				};
			}
			var justMyCode = GetConfigValue<bool?>(arguments.ConfigurationProperties, "justMyCode") ?? true;
			try
			{
				if (remoteAttachInfo is null) _debugger.Attach(processId.Value, justMyCode);
				else _debugger.AttachRemote(remoteAttachInfo, justMyCode);
				return new AttachResponse();
			}
			catch (Exception ex)
			{
				_logger?.Invoke($"Attach failed: {ex.Message}");
				throw new ProtocolException($"Failed to attach: {ex.Message}");
			}
		});
	}

	protected override async void HandleConfigurationDoneRequestAsync(IRequestResponder<ConfigurationDoneArguments> responder)
	{
		try
		{
			_logger?.Invoke("Configuration done");
			await _debugger.ConfigurationDone();
			responder.SetResponse(new ConfigurationDoneResponse());
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"HandleConfigurationDoneRequestAsync failed: {ex.Message} , {ex}");
			responder.SetError(new ProtocolException($"ConfigurationDone failed: {ex.Message}", ex));
		}
	}

	protected override SetBreakpointsResponse HandleSetBreakpointsRequest(SetBreakpointsArguments arguments)
	{
		return ExecuteWithExceptionHandling(() =>
		{
			if (arguments.Source?.Path is null)
			{
				throw new ProtocolException("Missing source path");
			}

			var breakpointRequests = arguments.Breakpoints?
				.Select(bp => new SharpDbgBreakpointRequest(
					ConvertClientLineToDebugger(bp.Line),
					bp.Condition,
					bp.HitCondition,
					bp.Column is null ? null : ConvertClientColumnToDebugger(bp.Column.Value)))
				.ToArray() ?? [];

			var breakpoints = _debugger.SetBreakpoints(arguments.Source.Path, breakpointRequests);

			var responseBreakpoints = breakpoints.Select(bp => new MSBreakpoint
			{
				Id = bp.Id,
				Verified = bp.Verified,
				Line = ConvertDebuggerLineToClient(bp.Line),
				Column = bp is { Verified: true, Column: not null } ? ConvertDebuggerColumnToClient(bp.Column.Value) : null,
				EndLine = bp.Verified ? bp.EndLine : null,
				EndColumn = bp is { Verified: true, EndColumn: not null } ? ConvertDebuggerColumnToClient(bp.EndColumn.Value) : null,
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
		return ExecuteWithExceptionHandling(() =>
		{
			var requests = arguments.Breakpoints?.Select(bp =>
				new SharpDbgFunctionBreakpointRequest(bp.Name, bp.Condition, bp.HitCondition)).ToArray() ?? [];
			var breakpoints = _debugger.SetFunctionBreakpoints(requests);
			return new SetFunctionBreakpointsResponse
			{
				Breakpoints = breakpoints.Select(bp => new MSBreakpoint
				{
					Id = bp.Id,
					Verified = bp.Verified,
					Message = bp.Message,
					Line = null,
					Column = null,
					EndLine = null,
					EndColumn = null,
					Source = null
				}).ToList()
			};
		});
	}

	protected override SetExceptionBreakpointsResponse HandleSetExceptionBreakpointsRequest(SetExceptionBreakpointsArguments arguments)
	{
		return ExecuteWithExceptionHandling(() =>
		{
			_logger?.Invoke($"Exception breakpoints: {string.Join(", ", arguments?.Filters ?? [])}");

			return new SetExceptionBreakpointsResponse();
		});
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
				EndColumn = ConvertDebuggerColumnToClient(f.EndColumn),
				Source = f.Source is not null ? new Source { Path = f.Source, Name = Path.GetFileName(f.Source), SourceReference = 0 } : null
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
			_debugger.HandleContinueRequest();
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

	protected override async void HandleExceptionInfoRequestAsync(IRequestResponder<ExceptionInfoArguments, ExceptionInfoResponse> responder)
	{
		try
		{
			var threadId = responder.Arguments.ThreadId;
			var exceptionInfo = await _debugger.ExceptionInfo(new ThreadId(threadId));

			var response = new ExceptionInfoResponse
			{
				ExceptionId = exceptionInfo.ExceptionId,
				Description = exceptionInfo.Description,
				BreakMode = exceptionInfo.BreakMode switch
				{
					SharpDbgExceptionBreakMode.Always => ExceptionBreakMode.Always,
					SharpDbgExceptionBreakMode.Unhandled => ExceptionBreakMode.Unhandled,
					SharpDbgExceptionBreakMode.UserUnhandled => ExceptionBreakMode.UserUnhandled,
					_ => ExceptionBreakMode.Unknown
				},
				Code = exceptionInfo.Code,
				Details = new ExceptionDetails
				{
					Message = exceptionInfo.Details.Message,
					TypeName = exceptionInfo.Details.TypeName,
					FullTypeName = exceptionInfo.Details.FullTypeName,
					EvaluateName = exceptionInfo.Details.EvaluateName,
					StackTrace = exceptionInfo.Details.StackTrace,
					InnerException = exceptionInfo.Details.InnerException.Select(inner => new ExceptionDetails
					{
						Message = inner.Message,
						TypeName = inner.TypeName,
						FullTypeName = inner.FullTypeName,
						EvaluateName = inner.EvaluateName,
						StackTrace = inner.StackTrace,
						InnerException = [] // Only support one level of inner exceptions for now
					}).ToList(),
					FormattedDescription = exceptionInfo.Details.FormattedDescription,
					HResult = exceptionInfo.Details.HResult,
					Source = exceptionInfo.Details.Source
				}
			};
			responder.SetResponse(response);
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"HandleExceptionInfoRequestAsync failed: {ex.Message} , {ex}");
			responder.SetError(new ProtocolException($"Failed to get exception info: {ex.Message}", ex));
		}
	}

	protected override GotoResponse HandleGotoRequest(GotoArguments arguments)
	{
		return base.HandleGotoRequest(arguments);
	}

	protected override GotoTargetsResponse HandleGotoTargetsRequest(GotoTargetsArguments arguments)
	{
		return base.HandleGotoTargetsRequest(arguments);
		var response = new GotoTargetsResponse
		{
			Targets =
			[
				new GotoTarget
				{
					Id = 0,
					Label = null,
					Line = 0,
					Column = null,
					EndLine = null,
					EndColumn = null,
					InstructionPointerReference = null
				}
			]
		};
		return response;
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
