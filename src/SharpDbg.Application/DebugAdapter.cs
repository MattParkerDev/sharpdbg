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
		Protocol.RemoveRequestRegistration("vsCustomMessage");
		Protocol.RegisterRequestType<VsCustomMessageRequest, VsCustomMessageArguments, VsCustomMessageResponse>(responder =>
		{
			responder.SetResponse(new VsCustomMessageResponse());
		});
	}

	private async Task<T> ExecuteWithExceptionHandling<T>(Func<T> func)
	{
		try
		{
			using (await _debugger.DapRequestAndRuntimeEventLock.LockAsync())
			{
				await _debugger.DrainRuntimeEventQueue();
				return func();
			}
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

	private async Task<T> ExecuteWithDebuggerProcessingLockAsync<T>(Func<Task<T>> func)
	{
		using (await _debugger.DapRequestAndRuntimeEventLock.LockAsync())
		{
			await _debugger.DrainRuntimeEventQueue();
			return await func().ConfigureAwait(false);
		}
	}

	private async Task ExecuteWithDebuggerProcessingLockAsync(Func<Task> func)
	{
		using (await _debugger.DapRequestAndRuntimeEventLock.LockAsync())
		{
			await _debugger.DrainRuntimeEventQueue();
			await func().ConfigureAwait(false);
		}
	}

	private async Task<T> ExecuteWithDebuggerProcessingLockAsync<T>(Func<T> func)
	{
		using (await _debugger.DapRequestAndRuntimeEventLock.LockAsync())
		{
			await _debugger.DrainRuntimeEventQueue();
			return func();
		}
	}

	private async Task ExecuteWithDebuggerProcessingLockAsync(Action func)
	{
		using (await _debugger.DapRequestAndRuntimeEventLock.LockAsync())
		{
			await _debugger.DrainRuntimeEventQueue();
			func();
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

		_debugger.OnStopped2 += (threadId, filePath, line, column, reason, hitBreakpointIds, decompiledSourceInfo) =>
		{
			var source = new Source { Path = filePath };
			var stoppedEvent = new StoppedEvent
			{
				Reason = ConvertStopReason(reason),
				ThreadId = threadId,
				AllThreadsStopped = true,
				HitBreakpointIds = hitBreakpointIds
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
			Protocol.SendEvent(new ContinuedEvent
			{
				ThreadId = threadId,
				AllThreadsContinued = true
			});
		};

		_debugger.OnExited += exitCode =>
		{
			// exitCode will be null in the attach scenario, as we cannot obtain the exit code of the process we attached to
			Protocol.SendEvent(new ExitedEvent
			{
				ExitCode = exitCode ?? 0
			});
		};

		_debugger.OnTerminated += () =>
		{
			Protocol.SendEvent(new TerminatedEvent());
		};

		_debugger.OnThreadStarted += threadId =>
		{
			Protocol.SendEvent(new ThreadEvent
			{
				Reason = ThreadEvent.ReasonValue.Started,
				ThreadId = threadId
			});
		};

		_debugger.OnThreadExited += threadId =>
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
		_debugger.OnModuleUnloaded += (id, name, path) =>
		{
			Protocol.SendEvent(new ModuleEvent
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
			Protocol.SendEvent(new ProcessEvent
			{
				Name = name,
				SystemProcessId = processId,
				StartMethod = ProcessEvent.StartMethodValue.Launch,
				IsLocalProcess = true
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
			var programForTitle = launchInfo.Program is "dotnet" ? launchInfo.Arguments[0] : launchInfo.Program;
			var runInTerminalRequest = new RunInTerminalRequest
			{
				Kind = launchInfo.LaunchRequestConsoleType switch
				{
					LaunchRequestConsoleType.IntegratedTerminal => RunInTerminalArguments.KindValue.Integrated,
					LaunchRequestConsoleType.ExternalTerminal => RunInTerminalArguments.KindValue.External,
					_ => throw new ArgumentOutOfRangeException(nameof(launchInfo.LaunchRequestConsoleType), $"Invalid LaunchRequestConsoleType for RunInTerminalRequest: '{launchInfo.LaunchRequestConsoleType}'")
				},
				Arguments = [launchInfo.Program, ..launchInfo.Arguments],
				Cwd = launchInfo.Cwd,
				Env = launchInfo.Env.ToDictionary(kvp => kvp.Key, kvp => (object)kvp.Value),
				Title = $"{Path.GetFileName(programForTitle)} [DEBUG]"
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
		// This event MUST be sent after the InitializeResponse. We achieve that currently because DebugAdapter queues events for synchronous handlers such as this one.
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

	protected override async void HandleLaunchRequestAsync(IRequestResponder<LaunchArguments> responder)
	{
		try
		{
			var arguments = responder.Arguments;
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
				LaunchRequestConsoleType = launchRequestConsoleType
			};

			await ExecuteWithDebuggerProcessingLockAsync(async () => _debugger.Launch(launchInfo, justMyCode));
			responder.SetResponse(new LaunchResponse());
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"HandleLaunchRequestAsync failed: {ex.Message} , {ex}");
			responder.SetError(new ProtocolException($"Failed to launch: {ex.Message}", ex));
		}
	}

	protected override async void HandleAttachRequestAsync(IRequestResponder<AttachArguments> responder)
	{
		try
		{
			var arguments = responder.Arguments;
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
			await ExecuteWithDebuggerProcessingLockAsync(async () =>
			{
				if (remoteAttachInfo is null) _debugger.Attach(processId.Value, justMyCode);
				else _debugger.AttachRemote(remoteAttachInfo, justMyCode);
			});
			responder.SetResponse(new AttachResponse());
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"HandleAttachRequestAsync failed: {ex.Message} , {ex}");
			responder.SetError(new ProtocolException($"Failed to attach: {ex.Message}", ex));
		}
	}

	protected override async void HandleConfigurationDoneRequestAsync(IRequestResponder<ConfigurationDoneArguments> responder)
	{
		try
		{
			_logger?.Invoke("Configuration done");
			await ExecuteWithDebuggerProcessingLockAsync(() => _debugger.ConfigurationDone());
			responder.SetResponse(new ConfigurationDoneResponse());
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"HandleConfigurationDoneRequestAsync failed: {ex.Message} , {ex}");
			responder.SetError(new ProtocolException($"ConfigurationDone failed: {ex.Message}", ex));
		}
	}

	protected override async void HandleSetBreakpointsRequestAsync(IRequestResponder<SetBreakpointsArguments, SetBreakpointsResponse> responder)
	{
		try
		{
			var arguments = responder.Arguments;
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

			var breakpoints = await ExecuteWithDebuggerProcessingLockAsync(async () => _debugger.SetBreakpoints(arguments.Source.Path, breakpointRequests));

			var responseBreakpoints = breakpoints.Select(bp => new MSBreakpoint
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
			}).ToList();

			responder.SetResponse(new SetBreakpointsResponse
			{
				Breakpoints = responseBreakpoints
			});
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"HandleSetBreakpointsRequestAsync failed: {ex.Message} , {ex}");
			responder.SetError(new ProtocolException($"Failed to set breakpoints: {ex.Message}", ex));
		}
	}

	protected override async void HandleSetFunctionBreakpointsRequestAsync(IRequestResponder<SetFunctionBreakpointsArguments, SetFunctionBreakpointsResponse> responder)
	{
		try
		{
			var arguments = responder.Arguments;
			var requests = arguments.Breakpoints?.Select(bp =>
				new SharpDbgFunctionBreakpointRequest(bp.Name, bp.Condition, bp.HitCondition)).ToArray() ?? [];
			var breakpoints = await ExecuteWithDebuggerProcessingLockAsync(() => _debugger.SetFunctionBreakpoints(requests));
			responder.SetResponse(new SetFunctionBreakpointsResponse
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
			});
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"HandleSetFunctionBreakpointsRequestAsync failed: {ex.Message} , {ex}");
			responder.SetError(new ProtocolException($"Failed to set function breakpoints: {ex.Message}", ex));
		}
	}

	protected override async void HandleSetExceptionBreakpointsRequestAsync(IRequestResponder<SetExceptionBreakpointsArguments, SetExceptionBreakpointsResponse> responder)
	{
		try
		{
			var arguments = responder.Arguments;
			var breakpointRequests = new List<SharpDbgExceptionBreakpointRequest>();
			foreach (var filter in arguments.Filters ?? [])
			{
				breakpointRequests.Add(new SharpDbgExceptionBreakpointRequest(ParseExceptionFilter(filter)));
			}
			foreach (var option in arguments.FilterOptions ?? [])
			{
				breakpointRequests.Add(new SharpDbgExceptionBreakpointRequest(ParseExceptionFilter(option.FilterId), option.Condition));
			}
			_logger?.Invoke($"Exception breakpoints: {string.Join(", ", breakpointRequests)}");
			await ExecuteWithDebuggerProcessingLockAsync(() => _debugger.SetExceptionBreakpoints(breakpointRequests));

			responder.SetResponse(new SetExceptionBreakpointsResponse());
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"HandleSetExceptionBreakpointsRequestAsync failed: {ex.Message} , {ex}");
			responder.SetError(new ProtocolException($"Failed to set exception breakpoints: {ex.Message}", ex));
		}

		static SharpDbgExceptionBreakpointFilter ParseExceptionFilter(string filter) => filter switch
		{
			"all" => SharpDbgExceptionBreakpointFilter.All,
			"user-unhandled" => SharpDbgExceptionBreakpointFilter.UserUnhandled,
			_ => throw new ProtocolException($"Unknown exception breakpoint filter: '{filter}'")
		};
	}

	protected override async void HandleThreadsRequestAsync(IRequestResponder<ThreadsArguments, ThreadsResponse> responder)
	{
		try
		{
			var threads = await ExecuteWithDebuggerProcessingLockAsync(() => _debugger.GetThreads());

			var responseThreads = threads.Select(t => new MSThread
			{
				Id = t.id,
				Name = t.name
			}).ToList();

			responder.SetResponse(new ThreadsResponse
			{
				Threads = responseThreads
			});
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"HandleThreadsRequestAsync failed: {ex.Message} , {ex}");
			responder.SetError(new ProtocolException($"Failed to get threads: {ex.Message}", ex));
		}
	}

	protected override async void HandleStackTraceRequestAsync(IRequestResponder<StackTraceArguments, StackTraceResponse> responder)
	{
		try
		{
			var arguments = responder.Arguments;
			var frames = await ExecuteWithDebuggerProcessingLockAsync(() => _debugger.GetStackTrace(arguments.ThreadId, arguments.StartFrame ?? 0, arguments.Levels));

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

			responder.SetResponse(new StackTraceResponse
			{
				StackFrames = responseFrames
			});
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"HandleStackTraceRequestAsync failed: {ex.Message} , {ex}");
			responder.SetError(new ProtocolException($"Failed to get stack trace: {ex.Message}", ex));
		}
	}

	protected override async void HandleScopesRequestAsync(IRequestResponder<ScopesArguments, ScopesResponse> responder)
	{
		try
		{
			var scopes = await ExecuteWithDebuggerProcessingLockAsync(() => _debugger.GetScopes(responder.Arguments.FrameId));

			var responseScopes = scopes.Select(s => new Scope
			{
				Name = s.Name,
				VariablesReference = s.VariablesReference,
				Expensive = s.Expensive
			}).ToList();

			responder.SetResponse(new ScopesResponse
			{
				Scopes = responseScopes
			});
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"HandleScopesRequestAsync failed: {ex.Message} , {ex}");
			responder.SetError(new ProtocolException($"Failed to get scopes: {ex.Message}", ex));
		}
	}

	protected override async void HandleVariablesRequestAsync(IRequestResponder<VariablesArguments, VariablesResponse> responder)
	{
		try
		{
			var variables = await ExecuteWithDebuggerProcessingLockAsync(() => _debugger.GetVariables(responder.Arguments.VariablesReference));

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
			var variableInfo = await ExecuteWithDebuggerProcessingLockAsync(() => _debugger.Evaluate(arguments.Expression, arguments.FrameId));

			var response = new EvaluateResponse
			{
				Result = variableInfo.Value,
				Type = variableInfo.Type,
				VariablesReference = variableInfo.VariablesReference,
				PresentationHint = variableInfo.PresentationHint?.ToDto()
			};
			responder.SetResponse(response);
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"HandleEvaluateRequestAsync failed: {ex.Message} , {ex}");
			responder.SetError(new ProtocolException($"Failed to evaluate expression: {ex.Message}", ex));
		}
	}

	protected override async void HandleContinueRequestAsync(IRequestResponder<ContinueArguments, ContinueResponse> responder)
	{
		try
		{
			await ExecuteWithDebuggerProcessingLockAsync(() => _debugger.HandleContinueRequest());
			responder.SetResponse(new ContinueResponse
			{
				AllThreadsContinued = true
			});
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"HandleContinueRequestAsync failed: {ex.Message} , {ex}");
			responder.SetError(new ProtocolException($"Failed to continue: {ex.Message}", ex));
		}
	}

	protected override async void HandleNextRequestAsync(IRequestResponder<NextArguments> responder)
	{
		try
		{
			await ExecuteWithDebuggerProcessingLockAsync(() => _debugger.StepNext(responder.Arguments.ThreadId));
			responder.SetResponse(new NextResponse());
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"HandleNextRequestAsync failed: {ex.Message} , {ex}");
			responder.SetError(new ProtocolException($"Failed to step: {ex.Message}", ex));
		}
	}

	protected override async void HandleStepInRequestAsync(IRequestResponder<StepInArguments> responder)
	{
		try
		{
			await ExecuteWithDebuggerProcessingLockAsync(() => _debugger.StepIn(responder.Arguments.ThreadId));
			responder.SetResponse(new StepInResponse());
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"HandleStepInRequestAsync failed: {ex.Message} , {ex}");
			responder.SetError(new ProtocolException($"Failed to step in: {ex.Message}", ex));
		}
	}

	protected override async void HandleStepOutRequestAsync(IRequestResponder<StepOutArguments> responder)
	{
		try
		{
			await ExecuteWithDebuggerProcessingLockAsync(() => _debugger.StepOut(responder.Arguments.ThreadId));
			responder.SetResponse(new StepOutResponse());
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"HandleStepOutRequestAsync failed: {ex.Message} , {ex}");
			responder.SetError(new ProtocolException($"Failed to step out: {ex.Message}", ex));
		}
	}

	protected override async void HandlePauseRequestAsync(IRequestResponder<PauseArguments> responder)
	{
		try
		{
			await ExecuteWithDebuggerProcessingLockAsync(() => _debugger.Pause());
			responder.SetResponse(new PauseResponse());
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"HandlePauseRequestAsync failed: {ex.Message} , {ex}");
			responder.SetError(new ProtocolException($"Failed to pause: {ex.Message}", ex));
		}
	}

	protected override async void HandleDisconnectRequestAsync(IRequestResponder<DisconnectArguments> responder)
	{
		try
		{
			await ExecuteWithDebuggerProcessingLockAsync(() => _debugger.Disconnect(responder.Arguments?.TerminateDebuggee ?? false));
			responder.SetResponse(new DisconnectResponse());
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"HandleDisconnectRequestAsync failed: {ex.Message} , {ex}");
			responder.SetError(new ProtocolException($"Failed to disconnect: {ex.Message}", ex));
		}
	}

	protected override async void HandleTerminateRequestAsync(IRequestResponder<TerminateArguments> responder)
	{
		try
		{
			await ExecuteWithDebuggerProcessingLockAsync(() => _debugger.Terminate());
			responder.SetResponse(new TerminateResponse());
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"HandleTerminateRequestAsync failed: {ex.Message} , {ex}");
			responder.SetError(new ProtocolException($"Failed to terminate: {ex.Message}", ex));
		}
	}

	protected override async void HandleExceptionInfoRequestAsync(IRequestResponder<ExceptionInfoArguments, ExceptionInfoResponse> responder)
	{
		try
		{
			var threadId = responder.Arguments.ThreadId;
			var exceptionInfo = await ExecuteWithDebuggerProcessingLockAsync(() => _debugger.ExceptionInfo(new ThreadId(threadId)));

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
