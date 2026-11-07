using System.Text.Json;
using System.Text.Json.Nodes;
using Ardalis.GuardClauses;
using SharpDbg.Application.Protocol;
using SharpDbg.Infrastructure.Debugger;
using SharpDbg.Infrastructure.Debugger.Models;
using SharpDbg.Infrastructure.Debugger.Models.Response;
using SharpDbg.DapProtocol;
using static SharpDbg.DapProtocol.DapResponse;
using DapBreakpoint = SharpDbg.DapProtocol.Breakpoint;
using DapStackFrame = SharpDbg.DapProtocol.StackFrame;
using DapThread = SharpDbg.DapProtocol.Thread;

namespace SharpDbg.Application;

/// <summary>
/// Main debug adapter that coordinates DAP protocol and debugger engine
/// </summary>
public class DebugAdapter : IDapRequestHandler
{
	private readonly ManagedDebugger _debugger;
	private readonly Action<string>? _logger;
	private bool _clientLinesStartAt1 = true;
	private bool _clientColumnsStartAt1 = true;
	private int _shutdownRequested;

	/// <summary>
	/// Raised once the debug session has ended (a disconnect request has been fully handled).
	/// Hosts which own the process lifetime (e.g. SharpDbg.Cli) should subscribe and exit when it fires.
	/// </summary>
	public event Action? ShutdownRequested;

	public DapConnection Connection { get; private set; } = null!;

	public DebugAdapter(Action<string>? logger = null)
	{
		_logger = logger;
		_debugger = new ManagedDebugger(logger);

		// Subscribe to debugger events
		SubscribeToDebuggerEvents();
	}

	/// <summary>
	/// Signals the end of the debug session and raises <see cref="ShutdownRequested"/> so the hosting
	/// process can shut down. Idempotent.
	/// Only called once a 'disconnect' request has been fully handled - 'terminate' must keep the adapter alive.
	/// </summary>
	private void RequestDebuggerProcessShutdown()
	{
		if (Interlocked.Exchange(ref _shutdownRequested, 1) is not 0) return;

		_logger?.Invoke("RequestDebuggerProcessShutdown");
		ShutdownRequested?.Invoke();
	}

	/// <summary>
	/// Releases debugger resources when the session ends without a disconnect request having been received
	/// (e.g. the client crashed or closed its stream)
	/// </summary>
	public void DapAborted_ShutdownDebugger()
	{
		try
		{
			// No-op if a disconnect request was already handled - Disconnect only disposes once
			_debugger.Disconnect(true);
		}
		catch { /* */ }
	}

	/// <summary>
	/// Creates the protocol connection over the given streams. Call <see cref="DapConnection.Start"/> to begin processing messages.
	/// </summary>
	public void Initialize(Stream input, Stream output)
	{
		Connection = new DapConnection(input, output, this, new DapConnectionOptions { Log = _logger });
		Connection.RegisterRequest<ResolveStackFrameArguments>();
		Connection.RegisterRequest<VsCustomMessageArguments>();
	}

	public ValueTask<DapResponse> HandleRequestAsync(IDapRequest request, DapRequestContext context) => request switch
	{
		InitializeArguments args => Respond(args, a => HandleInitialize(a, context)),
		LaunchArguments args => Respond(args, HandleLaunchAsync),
		AttachArguments args => Respond(args, HandleAttachAsync),
		ConfigurationDoneArguments args => Respond(args, HandleConfigurationDoneAsync),
		SetBreakpointsArguments args => Respond(args, HandleSetBreakpointsAsync),
		SetFunctionBreakpointsArguments args => Respond(args, HandleSetFunctionBreakpointsAsync),
		SetExceptionBreakpointsArguments args => Respond(args, HandleSetExceptionBreakpointsAsync),
		ThreadsArguments args => Respond(args, HandleThreadsAsync),
		StackTraceArguments args => Respond(args, HandleStackTraceAsync),
		ResolveStackFrameArguments args => Respond(args, HandleResolveStackFrameAsync),
		ScopesArguments args => Respond(args, HandleScopesAsync),
		VariablesArguments args => Respond(args, HandleVariablesAsync),
		EvaluateArguments args => Respond(args, HandleEvaluateAsync),
		ContinueArguments args => Respond(args, HandleContinueAsync),
		NextArguments args => Respond(args, HandleNextAsync),
		StepInArguments args => Respond(args, HandleStepInAsync),
		StepOutArguments args => Respond(args, HandleStepOutAsync),
		PauseArguments args => Respond(args, HandlePauseAsync),
		DisconnectArguments args => Respond(args, a => HandleDisconnectAsync(a, context)),
		TerminateArguments args => Respond(args, HandleTerminateAsync),
		ExceptionInfoArguments args => Respond(args, HandleExceptionInfoAsync),
		VsCustomMessageArguments args => Respond(args, HandleVsCustomMessage),
		_ => NotSupported(request),
	};

	/// <summary>
	/// Runs <paramref name="func"/> while holding the debugger's request/runtime-event lock, converting any exception
	/// thrown by the debugger engine into an error response.
	/// </summary>
	private ValueTask<DapResult<T>> RunLocked<T>(string failureMessage, Func<DapResult<T>> func)
	{
		return RunLocked(failureMessage, () => ValueTask.FromResult(func()));
	}

	private async ValueTask<DapResult<T>> RunLocked<T>(string failureMessage, Func<ValueTask<DapResult<T>>> func)
	{
		try
		{
			using (await _debugger.DapRequestAndRuntimeEventLock.LockAsync())
			{
				await _debugger.DrainRuntimeEventQueue();
				return await func().ConfigureAwait(false);
			}
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"{failureMessage}: {ex.Message} , {ex}");
			return new DapError($"{failureMessage}: {ex.Message}");
		}
	}

	private static T? GetConfigValue<T>(JsonObject? config, string key)
	{
		if (config?.TryGetPropertyValue(key, out var node) is true && node is not null)
		{
			return node.Deserialize<T>(ConfigurationJsonOptions);
		}
		return default;
	}

	// Matches the leniency of the previous implementation, e.g. VS Code's process picker sends processId as a string
	private static readonly JsonSerializerOptions ConfigurationJsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
	};

	private void SubscribeToDebuggerEvents()
	{
		_debugger.OnStopped += (threadId, reason) =>
		{
			Connection.SendEvent(new StoppedEvent
			{
				Reason = ConvertStopReason(reason),
				ThreadId = threadId,
				AllThreadsStopped = true
			});
		};

		_debugger.OnStopped2 += (threadId, filePath, line, column, reason, hitBreakpointIds) =>
		{
			Connection.SendEvent(new StoppedEvent
			{
				Reason = ConvertStopReason(reason),
				ThreadId = threadId,
				AllThreadsStopped = true,
				HitBreakpointIds = hitBreakpointIds,
				// Not part of the DAP specification - lets clients show the stop location without a stackTrace request
				AdditionalProperties = new JsonObject
				{
					["source"] = new JsonObject { ["path"] = filePath },
					["line"] = line,
					["column"] = column
				}
			});
		};

		_debugger.OnBreakpointChanged += breakpoint =>
		{
			Connection.SendEvent(new BreakpointEvent
			{
				Reason = BreakpointEvent.ReasonValue.Changed,
				Breakpoint = new DapBreakpoint
				{
					Id = breakpoint.Id,
					Verified = breakpoint.Verified,
					Line = breakpoint.IsFunctionBreakpoint ? null : ConvertDebuggerLineToClient(breakpoint.Line),
					Column = breakpoint is { IsFunctionBreakpoint: false, Verified: true, Column: not null } ? ConvertDebuggerColumnToClient(breakpoint.Column.Value) : null,
					EndLine = breakpoint is { IsFunctionBreakpoint: false, Verified: true } ? ConvertDebuggerLineToClient(breakpoint.EndLine) : null,
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
			Connection.SendEvent(new ContinuedEvent
			{
				ThreadId = threadId,
				AllThreadsContinued = true
			});
		};

		_debugger.OnExited += exitCode =>
		{
			// exitCode will be null in the attach scenario, as we cannot obtain the exit code of the process we attached to
			Connection.SendEvent(new ExitedEvent
			{
				ExitCode = exitCode ?? 0
			});
		};

		_debugger.OnTerminated += () =>
		{
			Connection.SendEvent(new TerminatedEvent());
		};

		_debugger.OnThreadStarted += threadId =>
		{
			Connection.SendEvent(new ThreadEvent
			{
				Reason = ThreadEvent.ReasonValue.Started,
				ThreadId = threadId
			});
		};

		_debugger.OnThreadExited += threadId =>
		{
			Connection.SendEvent(new ThreadEvent
			{
				Reason = ThreadEvent.ReasonValue.Exited,
				ThreadId = threadId
			});
		};

		_debugger.OnModuleLoaded += (id, name, path) =>
		{
			Connection.SendEvent(new ModuleEvent
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
		_debugger.OnModuleUnloaded += (id, name, path) =>
		{
			Connection.SendEvent(new ModuleEvent
			{
				Reason = ModuleEvent.ReasonValue.Removed,
				Module = new Module
				{
					Id = id,
					Name = name,
					Path = path
				}
			});
		};

		_debugger.OnProcessStarted += (processId, name) =>
		{
			Connection.SendEvent(new ProcessEvent
			{
				Name = name,
				SystemProcessId = processId,
				StartMethod = ProcessEvent.StartMethodValue.Launch,
				IsLocalProcess = true
			});
		};

		_debugger.OnOutput += (output, isStdError) =>
		{
			Connection.SendEvent(new OutputEvent
			{
				Category = isStdError ? OutputEvent.CategoryValue.Stderr : OutputEvent.CategoryValue.Stdout,
				Output = output
			});
		};
		_debugger.OnImportantOutput += output =>
		{
			Connection.SendEvent(new OutputEvent
			{
				Category = OutputEvent.CategoryValue.Important,
				Output = output
			});
		};
		_debugger.SendRunInTerminalRequest += launchInfo =>
		{
			var programForTitle = launchInfo.Program is "dotnet" ? launchInfo.Arguments[0] : launchInfo.Program;
			var runInTerminalArguments = new RunInTerminalArguments
			{
				Kind = launchInfo.LaunchRequestConsoleType switch
				{
					LaunchRequestConsoleType.IntegratedTerminal => RunInTerminalArguments.KindValue.Integrated,
					LaunchRequestConsoleType.ExternalTerminal => RunInTerminalArguments.KindValue.External,
					_ => throw new ArgumentOutOfRangeException(nameof(launchInfo.LaunchRequestConsoleType), $"Invalid LaunchRequestConsoleType for RunInTerminalRequest: '{launchInfo.LaunchRequestConsoleType}'")
				},
				Args = [launchInfo.Program, ..launchInfo.Arguments],
				Cwd = launchInfo.Cwd ?? Environment.CurrentDirectory,
				Env = new Dictionary<string, string?>(launchInfo.Env.Select(kvp => KeyValuePair.Create(kvp.Key, (string?)kvp.Value)))
				{
					["DOTNET_DefaultDiagnosticPortSuspend"] = "1"
				},
				Title = $"{Path.GetFileName(programForTitle)} [DEBUG]"
			};
			// Called synchronously from within a request handler (launch), which is safe as responses are read independently of request dispatch
			var result = Connection.SendRequest(runInTerminalArguments);
			return result switch
			{
				DapError error => throw new InvalidOperationException($"RunInTerminalRequest failed: {error.Message}"),
				// https://github.com/microsoft/vscode/issues/61640 - ProcessId will not be returned for integratedTerminal or externalTerminal
				// ShellProcessId will be returned for integratedTerminal but not externalTerminal
				RunInTerminalResponse { ProcessId: null } => throw new InvalidOperationException("RunInTerminalRequest did not return a process ID. VSCode does not return a process ID for integratedTerminal or externalTerminal. Use internalConsole instead, or use a compliant DAP client. See: https://github.com/microsoft/vscode/issues/61640"),
				RunInTerminalResponse response => response.ProcessId.Value,
				null => throw new InvalidOperationException("RunInTerminalRequest returned no result")
			};
		};
	}

	// Command handlers
	private static DapResult<VsCustomMessageResponse> HandleVsCustomMessage(VsCustomMessageArguments arguments) => new VsCustomMessageResponse();

	private DapResult<Capabilities> HandleInitialize(InitializeArguments arguments, DapRequestContext context)
	{
		_clientLinesStartAt1 = arguments.LinesStartAt1 ?? true;
		_clientColumnsStartAt1 = arguments.ColumnsStartAt1 ?? true;

		// The initialized event must be sent after the initialize response
		context.OnResponseSent(() => Connection.SendEvent(new InitializedEvent()));

		return new Capabilities
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
			SupportsExceptionFilterOptions = true,
			SupportsStepInTargetsRequest = false,
			SupportsGotoTargetsRequest = false,
			ExceptionBreakpointFilters =
			[
				new ExceptionBreakpointsFilter { Filter = "all", Label = "All Exceptions", Default = false },
				new ExceptionBreakpointsFilter { Filter = "user-unhandled", Label = "User-Unhandled Exceptions", Default = true }
			]
		};
	}

	private ValueTask<DapResult<LaunchResponse>> HandleLaunchAsync(LaunchArguments arguments)
	{
		return RunLocked<LaunchResponse>("Failed to launch", () =>
		{
			var config = arguments.AdditionalProperties;
			var program = GetConfigValue<string>(config, "program");
			if (string.IsNullOrEmpty(program))
			{
				return new DapError("Failed to launch: Missing program path");
			}

			var args = GetConfigValue<List<string>>(config, "args") ?? [];
			var cwd = GetConfigValue<string>(config, "cwd");
			var env = GetConfigValue<Dictionary<string, string>>(config, "env") ?? [];
			var stopAtEntry = GetConfigValue<bool?>(config, "stopAtEntry") ?? false;
			var justMyCode = GetConfigValue<bool?>(config, "justMyCode") ?? true;
			var console = GetConfigValue<string>(config, "console");
			LaunchRequestConsoleType? launchRequestConsoleType = console switch
			{
				"integratedTerminal" => LaunchRequestConsoleType.IntegratedTerminal,
				"externalTerminal" => LaunchRequestConsoleType.ExternalTerminal,
				"internalConsole" => LaunchRequestConsoleType.InternalConsole,
				null => LaunchRequestConsoleType.InternalConsole, // Default to internalConsole if not specified
				_ => null
			};
			if (launchRequestConsoleType is null) return new DapError($"Failed to launch: Invalid console type: '{console}'");

			(program, args) = Path.GetExtension(program).Equals(".dll", StringComparison.OrdinalIgnoreCase)
				? ("dotnet", [program, ..args])
				: (program, args);

			var launchInfo = new LaunchInfo
			{
				Program = program,
				Arguments = args,
				Cwd = cwd,
				Env = env,
				StopAtEntry = stopAtEntry,
				LaunchRequestConsoleType = launchRequestConsoleType.Value
			};

			_debugger.Launch(launchInfo, justMyCode);
			return new LaunchResponse();
		});
	}

	private ValueTask<DapResult<AttachResponse>> HandleAttachAsync(AttachArguments arguments)
	{
		return RunLocked<AttachResponse>("Failed to attach", () =>
		{
			var config = arguments.AdditionalProperties;
			RemoteAttachInfo? remoteAttachInfo = null;
			var processId = GetConfigValue<int?>(config, "processId");
			if (processId is null)
			{
				return new DapError("Failed to attach: Missing process ID");
			}
			var coreClrMobileDebuggerOptions = GetConfigValue<CoreClrMobileDebuggerOptions>(config, "coreClrMobileDebuggerOptions");
			if (coreClrMobileDebuggerOptions is not null)
			{
				Guard.Against.NullOrWhiteSpace(coreClrMobileDebuggerOptions.Address);
				Guard.Against.Null(coreClrMobileDebuggerOptions.Port);
				Guard.Against.NullOrWhiteSpace(coreClrMobileDebuggerOptions.Platform);
				Guard.Against.Null(coreClrMobileDebuggerOptions.IsServer);
				Guard.Against.NullOrWhiteSpace(coreClrMobileDebuggerOptions.MscordbiPath);
				Guard.Against.NullOrWhiteSpace(coreClrMobileDebuggerOptions.AssembliesPath);
				if (File.Exists(coreClrMobileDebuggerOptions.MscordbiPath) is false) return new DapError($"Failed to attach: coreClrMobileDebuggerOptions.mscordbiPath does not exist: {coreClrMobileDebuggerOptions.MscordbiPath}");
				if (Directory.Exists(coreClrMobileDebuggerOptions.AssembliesPath) is false) return new DapError($"Failed to attach: coreClrMobileDebuggerOptions.assembliesPath does not exist: {coreClrMobileDebuggerOptions.AssembliesPath}");
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
			var justMyCode = GetConfigValue<bool?>(config, "justMyCode") ?? true;
			if (remoteAttachInfo is null) _debugger.Attach(processId.Value, justMyCode);
			else _debugger.AttachRemote(remoteAttachInfo, justMyCode);
			return new AttachResponse();
		});
	}

	private ValueTask<DapResult<ConfigurationDoneResponse>> HandleConfigurationDoneAsync(ConfigurationDoneArguments arguments)
	{
		_logger?.Invoke("Configuration done");
		return RunLocked<ConfigurationDoneResponse>("ConfigurationDone failed", async () =>
		{
			await _debugger.ConfigurationDone();
			return new ConfigurationDoneResponse();
		});
	}

	private ValueTask<DapResult<SetBreakpointsResponse>> HandleSetBreakpointsAsync(SetBreakpointsArguments arguments)
	{
		if (arguments.Source.Path is not { } sourcePath)
		{
			return ValueTask.FromResult<DapResult<SetBreakpointsResponse>>(new DapError("Failed to set breakpoints: Missing source path"));
		}

		var breakpointRequests = arguments.Breakpoints?
			.Select(bp => new SharpDbgBreakpointRequest(
				ConvertClientLineToDebugger(bp.Line),
				bp.Condition,
				bp.HitCondition,
				bp.Column is null ? null : ConvertClientColumnToDebugger(bp.Column.Value)))
			.ToArray() ?? [];

		return RunLocked<SetBreakpointsResponse>("Failed to set breakpoints", () =>
		{
			var breakpoints = _debugger.SetBreakpoints(sourcePath, breakpointRequests);
			return new SetBreakpointsResponse
			{
				Breakpoints = breakpoints.Select(bp => new DapBreakpoint
				{
					Id = bp.Id,
					Verified = bp.Verified,
					Line = ConvertDebuggerLineToClient(bp.Line),
					Column = bp is { Verified: true, Column: not null } ? ConvertDebuggerColumnToClient(bp.Column.Value) : null,
					EndLine = bp.Verified ? ConvertDebuggerLineToClient(bp.EndLine) : null,
					EndColumn = bp is { Verified: true, EndColumn: not null } ? ConvertDebuggerColumnToClient(bp.EndColumn.Value) : null,
					Message = bp.Message,
					Source = new Source
					{
						Path = bp.FilePath
					}
				}).ToList()
			};
		});
	}

	private ValueTask<DapResult<SetFunctionBreakpointsResponse>> HandleSetFunctionBreakpointsAsync(SetFunctionBreakpointsArguments arguments)
	{
		var requests = arguments.Breakpoints.Select(bp => new SharpDbgFunctionBreakpointRequest(bp.Name, bp.Condition, bp.HitCondition)).ToArray();
		return RunLocked<SetFunctionBreakpointsResponse>("Failed to set function breakpoints", () =>
		{
			var breakpoints = _debugger.SetFunctionBreakpoints(requests);
			return new SetFunctionBreakpointsResponse
			{
				Breakpoints = breakpoints.Select(bp => new DapBreakpoint
				{
					Id = bp.Id,
					Verified = bp.Verified,
					Message = bp.Message
				}).ToList()
			};
		});
	}

	private ValueTask<DapResult<SetExceptionBreakpointsResponse>> HandleSetExceptionBreakpointsAsync(SetExceptionBreakpointsArguments arguments)
	{
		var breakpointRequests = new List<SharpDbgExceptionBreakpointRequest>();
		foreach (var filter in arguments.Filters)
		{
			if (ParseExceptionFilter(filter) is not { } parsedFilter) return UnknownFilter(filter);
			breakpointRequests.Add(new SharpDbgExceptionBreakpointRequest(parsedFilter));
		}
		foreach (var option in arguments.FilterOptions ?? [])
		{
			if (ParseExceptionFilter(option.FilterId) is not { } parsedFilter) return UnknownFilter(option.FilterId);
			breakpointRequests.Add(new SharpDbgExceptionBreakpointRequest(parsedFilter, option.Condition));
		}
		_logger?.Invoke($"Exception breakpoints: {string.Join(", ", breakpointRequests)}");

		return RunLocked<SetExceptionBreakpointsResponse>("Failed to set exception breakpoints", () =>
		{
			_debugger.SetExceptionBreakpoints(breakpointRequests);
			return new SetExceptionBreakpointsResponse();
		});

		static SharpDbgExceptionBreakpointFilter? ParseExceptionFilter(string filter) => filter switch
		{
			"all" => SharpDbgExceptionBreakpointFilter.All,
			"user-unhandled" => SharpDbgExceptionBreakpointFilter.UserUnhandled,
			_ => null
		};

		static ValueTask<DapResult<SetExceptionBreakpointsResponse>> UnknownFilter(string filter) =>
			ValueTask.FromResult<DapResult<SetExceptionBreakpointsResponse>>(new DapError($"Failed to set exception breakpoints: Unknown exception breakpoint filter: '{filter}'"));
	}

	private ValueTask<DapResult<ThreadsResponse>> HandleThreadsAsync(ThreadsArguments arguments)
	{
		return RunLocked<ThreadsResponse>("Failed to get threads", () => new ThreadsResponse
		{
			Threads = _debugger.GetThreads().Select(t => new DapThread
			{
				Id = t.id,
				Name = t.name
			}).ToList()
		});
	}

	private ValueTask<DapResult<StackTraceResponse>> HandleStackTraceAsync(StackTraceArguments arguments)
	{
		return RunLocked<StackTraceResponse>("Failed to get stack trace", () =>
		{
			var frames = _debugger.GetStackTrace(arguments.ThreadId, arguments.StartFrame ?? 0, arguments.Levels);
			return new StackTraceResponse
			{
				StackFrames = frames.Select(ToProtocolStackFrame).ToList()
			};
		});
	}

	private ValueTask<DapResult<ResolveStackFrameResponse>> HandleResolveStackFrameAsync(ResolveStackFrameArguments arguments)
	{
		return RunLocked<ResolveStackFrameResponse>("Failed to resolve stack frame", () =>
		{
			var frame = _debugger.ResolveStackFrame(arguments.StackFrameId);
			return new ResolveStackFrameResponse { StackFrame = ToProtocolStackFrame(frame) };
		});
	}

	private DapStackFrame ToProtocolStackFrame(StackFrameInfo frame)
	{
		var protocolFrame = new DapStackFrame
		{
			Id = frame.Id,
			Name = frame.Name,
			Line = ConvertDebuggerLineToClient(frame.Line),
			EndLine = frame.EndLine is int endLine ? ConvertDebuggerLineToClient(endLine) : null,
			Column = ConvertDebuggerColumnToClient(frame.Column),
			EndColumn = frame.EndColumn is int endColumn ? ConvertDebuggerColumnToClient(endColumn) : null,
			Source = frame.Source is not null ? new Source { Path = frame.Source, Name = Path.GetFileName(frame.Source), SourceReference = 0 } : null,
			PresentationHint = frame.IsUserCode ? null : (DapStackFrame.PresentationHintValue?)DapStackFrame.PresentationHintValue.Subtle
		};
		protocolFrame.IsResolved = frame.IsResolved;
		protocolFrame.DecompiledSourceInfo = frame.DecompiledSourceInfo;
		return protocolFrame;
	}

	private ValueTask<DapResult<ScopesResponse>> HandleScopesAsync(ScopesArguments arguments)
	{
		return RunLocked<ScopesResponse>("Failed to get scopes", () => new ScopesResponse
		{
			Scopes = _debugger.GetScopes(arguments.FrameId).Select(s => new Scope
			{
				Name = s.Name,
				VariablesReference = s.VariablesReference,
				Expensive = s.Expensive
			}).ToList()
		});
	}

	private ValueTask<DapResult<VariablesResponse>> HandleVariablesAsync(VariablesArguments arguments)
	{
		return RunLocked<VariablesResponse>("Failed to get variables", async () =>
		{
			var variables = await _debugger.GetVariables(arguments.VariablesReference);
			return new VariablesResponse
			{
				Variables = variables.Select(v => new Variable
				{
					Name = v.Name,
					EvaluateName = v.Name,
					Value = v.Value,
					Type = v.Type,
					PresentationHint = v.PresentationHint?.ToDto(),
					VariablesReference = v.VariablesReference
				}).ToList()
			};
		});
	}

	private ValueTask<DapResult<EvaluateResponse>> HandleEvaluateAsync(EvaluateArguments arguments)
	{
		return RunLocked<EvaluateResponse>("Failed to evaluate expression", async () =>
		{
			var variableInfo = await _debugger.Evaluate(arguments.Expression, arguments.FrameId);
			return new EvaluateResponse
			{
				Result = variableInfo.Value,
				Type = variableInfo.Type,
				VariablesReference = variableInfo.VariablesReference,
				PresentationHint = variableInfo.PresentationHint?.ToDto()
			};
		});
	}

	private ValueTask<DapResult<ContinueResponse>> HandleContinueAsync(ContinueArguments arguments)
	{
		return RunLocked<ContinueResponse>("Failed to continue", () =>
		{
			_debugger.HandleContinueRequest();
			return new ContinueResponse { AllThreadsContinued = true };
		});
	}

	private ValueTask<DapResult<NextResponse>> HandleNextAsync(NextArguments arguments)
	{
		return RunLocked<NextResponse>("Failed to step", async () =>
		{
			await _debugger.StepNext(arguments.ThreadId);
			return new NextResponse();
		});
	}

	private ValueTask<DapResult<StepInResponse>> HandleStepInAsync(StepInArguments arguments)
	{
		return RunLocked<StepInResponse>("Failed to step in", async () =>
		{
			await _debugger.StepIn(arguments.ThreadId);
			return new StepInResponse();
		});
	}

	private ValueTask<DapResult<StepOutResponse>> HandleStepOutAsync(StepOutArguments arguments)
	{
		return RunLocked<StepOutResponse>("Failed to step out", async () =>
		{
			await _debugger.StepOut(arguments.ThreadId);
			return new StepOutResponse();
		});
	}

	private ValueTask<DapResult<PauseResponse>> HandlePauseAsync(PauseArguments arguments)
	{
		return RunLocked<PauseResponse>("Failed to pause", () =>
		{
			_debugger.Pause();
			return new PauseResponse();
		});
	}

	private async ValueTask<DapResult<DisconnectResponse>> HandleDisconnectAsync(DisconnectArguments arguments, DapRequestContext context)
	{
		var result = await RunLocked<DisconnectResponse>("Failed to disconnect", () =>
		{
			_debugger.Disconnect(arguments.TerminateDebuggee ?? false);
			return new DisconnectResponse();
		});
		if (result is DisconnectResponse) context.OnResponseSent(RequestDebuggerProcessShutdown);
		return result;
	}

	private ValueTask<DapResult<TerminateResponse>> HandleTerminateAsync(TerminateArguments arguments)
	{
		return RunLocked<TerminateResponse>("Failed to terminate", () =>
		{
			_debugger.Terminate();
			return new TerminateResponse();
		});
	}

	private ValueTask<DapResult<ExceptionInfoResponse>> HandleExceptionInfoAsync(ExceptionInfoArguments arguments)
	{
		return RunLocked<ExceptionInfoResponse>("Failed to get exception info", async () =>
		{
			var exceptionInfo = await _debugger.ExceptionInfo(new ThreadId(arguments.ThreadId));
			return new ExceptionInfoResponse
			{
				ExceptionId = exceptionInfo.ExceptionId,
				Description = exceptionInfo.Description,
				BreakMode = exceptionInfo.BreakMode switch
				{
					SharpDbgExceptionBreakMode.Never => ExceptionBreakMode.Never,
					SharpDbgExceptionBreakMode.Always => ExceptionBreakMode.Always,
					SharpDbgExceptionBreakMode.Unhandled => ExceptionBreakMode.Unhandled,
					SharpDbgExceptionBreakMode.UserUnhandled => ExceptionBreakMode.UserUnhandled,
					_ => new ExceptionBreakMode("unknown")
				},
				Details = ToProtocolExceptionDetails(exceptionInfo.Details),
				// 'code' is a Visual Studio extension to the DAP specification
				AdditionalProperties = exceptionInfo.Code is 0 ? null : new JsonObject { ["code"] = exceptionInfo.Code }
			};
		});
	}

	private static ExceptionDetails ToProtocolExceptionDetails(ExceptionInfo.ExceptionDetails details)
	{
		// Visual Studio extensions to the DAP specification
		var additionalProperties = new JsonObject();
		if (details.FormattedDescription is not null) additionalProperties["formattedDescription"] = details.FormattedDescription;
		if (details.HResult is not 0) additionalProperties["hresult"] = details.HResult;
		if (details.Source is not null) additionalProperties["source"] = details.Source;

		return new ExceptionDetails
		{
			Message = details.Message,
			TypeName = details.TypeName,
			FullTypeName = details.FullTypeName,
			EvaluateName = details.EvaluateName,
			StackTrace = details.StackTrace,
			InnerException = details.InnerException.Select(inner => new ExceptionDetails
			{
				Message = inner.Message,
				TypeName = inner.TypeName,
				FullTypeName = inner.FullTypeName,
				EvaluateName = inner.EvaluateName,
				StackTrace = inner.StackTrace,
				InnerException = [] // Only support one level of inner exceptions for now
			}).ToList(),
			AdditionalProperties = additionalProperties
		};
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

	private static StoppedEvent.ReasonValue ConvertStopReason(string reason) => new(reason.ToLowerInvariant());
}
