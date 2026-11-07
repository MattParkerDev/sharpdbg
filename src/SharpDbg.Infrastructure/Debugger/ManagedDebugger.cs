using System.Diagnostics;
using System.Threading.Channels;
using Ardalis.GuardClauses;
using ICorDebugSharp;
using NeoSmart.AsyncLock;
using SharpDbg.Infrastructure.Debugger.ExpressionEvaluator.Cil;
using SharpDbg.Infrastructure.Debugger.Models;
using SharpDbg.Infrastructure.Debugger.Models.Response;

namespace SharpDbg.Infrastructure.Debugger;

// v1 of this class was AI generated, and could definitely do with some cleaning up
public partial class ManagedDebugger
{
	private ICorDebug? _corDebug;
	private ICorDebugProcess? _process;
	private readonly CorDebugManagedCallback _callbacks;
	private readonly BreakpointManager _breakpointManager;
	private readonly VariableManager _variableManager;
	private readonly FrameReferenceManager _frameReferenceManager;
	private readonly Action<string>? _logger;
	private sealed class ThreadInfo(ICorDebugThread thread)
	{
		public ICorDebugThread Thread { get; } = thread;
		public string? Name { get; set; }
	}

	private readonly Dictionary<int, ThreadInfo> _threads = new();
	private int? _mainThreadId;
	private int? _oldestThreadId;
	private readonly Dictionary<CORDB_ADDRESS, ModuleInfo> _modules = new();
	private readonly HashSet<COR_TYPEID> _initializedStaticTypes = [];
	private ICorDebugFunction? _suppressFinalizeFunction;
	/// <summary>
	/// Monotonically increasing version of the set of loaded debuggee modules. Incremented whenever a module
	/// is loaded or unloaded, so anything derived from <see cref="AllModules"/> (the expression compile cache and the
	/// metadata-blocks cache) can detect staleness and rebuild.
	/// </summary>
	internal int ModuleSet_Version { get; private set; }
	private bool _isAttached;
	private bool _isRemoteAttach;
	private int? _pendingAttachProcessId;
	private bool _justMyCode;
	private bool _stopAtEntry;
	private EntryBreakpoint? _entryBreakpoint;
	private sealed record EntryBreakpoint(ICorDebugFunctionBreakpoint CorBreakpoint, CORDB_ADDRESS ModuleBaseAddress, int MethodToken, int IlOffset);

	// The active exception filters and optional type conditions supplied by the DAP client.
	private IReadOnlyList<SharpDbgExceptionBreakpointRequest> _exceptionBreakpoints = [];
	// Per-thread state retained between callbacks for the same exception propagation sequence.
	private readonly Dictionary<ThreadId, ExceptionPropagationState> _exceptionStates = [];
	// The mode that caused each thread's latest exception stop, used by the exceptionInfo response.
	private readonly Dictionary<ThreadId, SharpDbgExceptionBreakMode> _exceptionBreakModes = [];
	private AsyncStepper? _asyncStepper;
	private CilExpressionEvaluator _expressionEvaluator = null!;

	private Process? _debuggeeProcess;

	public event Action<int, string>? OnStopped;
	// ThreadId, FilePath, Line, Column, Reason, HitBreakpointIds, DecompiledSourceInfo
	public event Action<int, string, int, int, string, List<int>?>? OnStopped2;
	public event Action<int>? OnContinued;
	public event Action<int?>? OnExited;
	public event Action? OnTerminated;
	public event Action<int>? OnThreadStarted;
	public event Action<int>? OnThreadExited;
	public event Action<string, string, string>? OnModuleLoaded;
	public event Action<string, string, string>? OnModuleUnloaded;
	// Output text, isError (true for stderr, false for stdout)
	public event Action<string, bool>? OnOutput;
	// Debugger messages the user should see, e.g. a breakpoint condition that failed to evaluate
	public event Action<string>? OnImportantOutput;
	public event Action<int, string>? OnProcessStarted;
	public event Action<BreakpointManager.BreakpointInfo>? OnBreakpointChanged;
	public event Func<LaunchInfo, int> SendRunInTerminalRequest = null!;

	public EvalStatus EvalStatus { get; }

	private Task? _runtimeEventCallbackProcessing;
	private readonly Channel<CorDebugManagedCallbackEventArgs> _runtimeEventChannel;
	public readonly AsyncLock DapRequestAndRuntimeEventLock = new();

	public ManagedDebugger(Action<string>? logger = null)
	{
		_logger = logger;
		_breakpointManager = new BreakpointManager();
		_variableManager = new VariableManager();
		_frameReferenceManager = new FrameReferenceManager();
		_callbacks = new CorDebugManagedCallback();
		EvalStatus = new EvalStatus();
		_asyncStepper = new AsyncStepper(_modules, this);
		_runtimeEventChannel = Channel.CreateUnbounded<CorDebugManagedCallbackEventArgs>(new UnboundedChannelOptions
		{
			SingleReader = false,
			SingleWriter = true
		});
		_callbacks.OnAnyEvent += QueueEvent;
		_runtimeEventCallbackProcessing = Task.Run(ProcessRuntimeEventQueue);
	}

	private void QueueEvent(object? sender, CorDebugManagedCallbackEventArgs e)
	{
		_runtimeEventChannel.Writer.TryWrite(e);
	}

	public async Task DrainRuntimeEventQueue()
	{
		// Caller should have obtained this lock, and we are re-entrant here
		using (await DapRequestAndRuntimeEventLock.LockAsync())
		{
			var reader = _runtimeEventChannel.Reader;
			// Process all immediately available events
			while (reader.TryRead(out var callbackEvent))
			{
				await OnAnyEvent(this, callbackEvent).ConfigureAwait(false);
			}
		}
	}

	private async Task ProcessRuntimeEventQueue()
	{
		try
		{
			var reader = _runtimeEventChannel.Reader;
			while (await reader.WaitToReadAsync())
			{
				// If a Dap request has obtained the lock, we will pause here. It will drain runtime events, and our TryRead may return false, which is fine
				using (await DapRequestAndRuntimeEventLock.LockAsync())
				{
					if (reader.TryRead(out var callbackEvent) is false) continue;
					await OnAnyEvent(this, callbackEvent).ConfigureAwait(false);
				}
			}
		}
		catch (Exception e)
		{
			_logger?.Invoke($"Critical failure processing runtime event queue, no further events will be processed: {e}");
			throw;
		}
	}

	internal async Task<CorDebugManagedCallbackEventArgs> ProcessRuntimeEventsUntilEvalEvent()
	{
		var reader = _runtimeEventChannel.Reader;
		while (await reader.WaitToReadAsync())
		{
			if (reader.TryRead(out var callbackEvent) is false) throw new InvalidOperationException("Expected to read an event from the runtime event queue, but none was available");
			await OnAnyEvent(this, callbackEvent).ConfigureAwait(false);
			if (callbackEvent is EvalCompleteCorDebugManagedCallbackEventArgs or EvalExceptionCorDebugManagedCallbackEventArgs)
			{
				return callbackEvent;
			}
			if (callbackEvent is ExitProcessCorDebugManagedCallbackEventArgs) throw new EvalException("The debuggee exited during evaluation");
		}
		throw new InvalidOperationException("Expected to read an eval event from the runtime event queue, but Channel completed unexpectedly");
	}

	private async Task OnAnyEvent(object? sender, CorDebugManagedCallbackEventArgs e)
	{
		try
		{
			_logger?.Invoke($"Event: {e.GetType().Name}");
			switch (e)
			{
				case LogMessageCorDebugManagedCallbackEventArgs a: HandleLogMessage(sender, a); break;
				case CreateProcessCorDebugManagedCallbackEventArgs a: HandleProcessCreated(sender, a); break;
				case ExitProcessCorDebugManagedCallbackEventArgs a: HandleProcessExited(sender, a); break;
				case CreateThreadCorDebugManagedCallbackEventArgs a: HandleThreadCreated(sender, a); break;
				case ExitThreadCorDebugManagedCallbackEventArgs a: HandleThreadExited(sender, a); break;
				case LoadModuleCorDebugManagedCallbackEventArgs a: HandleModuleLoaded(sender, a); break;
				case UnloadModuleCorDebugManagedCallbackEventArgs a: HandleModuleUnloaded(sender, a); break;
				case BreakpointCorDebugManagedCallbackEventArgs a: await HandleBreakpoint(sender, a).ConfigureAwait(false); break;
				case StepCompleteCorDebugManagedCallbackEventArgs a: await HandleStepComplete(sender, a).ConfigureAwait(false); break;
				case BreakCorDebugManagedCallbackEventArgs a: HandleBreak(sender, a); break;
				case ExceptionCorDebugManagedCallbackEventArgs a: HandleException(sender, a); break;
				case Exception2CorDebugManagedCallbackEventArgs a: HandleException2(sender, a); break;
				case EvalCompleteCorDebugManagedCallbackEventArgs or EvalExceptionCorDebugManagedCallbackEventArgs: break; // don't continue on these, as they are being used for expression evaluation
				default: _process?.Continue(false); break;
			}
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"Error handling event {e.GetType().Name}: {ex}");
			if (_process is not null && _process.TryIsRunning(out var isRunning) is Cor.S_OK && isRunning is false)
			{
				Continue();
			}
		}
	}

	private void HandleLogMessage(object? sender, LogMessageCorDebugManagedCallbackEventArgs logMessageEvent)
	{
		_logger?.Invoke($"Log: {logMessageEvent.Message}");
		Continue();
	}

	/// <summary>
	/// Actually attach to an existing process
	/// </summary>
	private void PerformAttach(int processId)
	{
		_logger?.Invoke($"Attaching to process: {processId}");

		// Initialize the debugger
		_ = Task.Run(async () =>
		{
			_corDebug = await ClrDebugExtensions.Automatic(processId);
			_corDebug.Initialize();
			_corDebug.SetManagedHandler(_callbacks);

			// Attach to the process
			_process = _corDebug.DebugActiveProcess(processId, false);
			_isAttached = true;
			ConfigureExceptionCallbacks();

			_logger?.Invoke($"Attached to process: {processId}");
			SendAllBreakpointEvents();
		});
	}

	private void PerformRemoteAttach(RemoteAttachInfo remoteAttachInfo)
	{
		_logger?.Invoke($"Attaching to remote process on {remoteAttachInfo.Address}:{remoteAttachInfo.Port}");

		_corDebug = ClrDebugExtensions.Mobile(remoteAttachInfo);
		_corDebug.SetManagedHandler(_callbacks);
		try
		{
			// It is expected that this does not return a ICorDebugProcess in the remote scenario - it is obtained via the CreateProcess callback instead
			// ClrDebug throws because it does not expect to receive a null pointer
			_ = _corDebug.DebugActiveProcess(0, false);
		} catch { /* */ }

		_logger?.Invoke($"Debugger listening on port {remoteAttachInfo.Port}, awaiting connection from debuggee");
		_ = Task.Run(SendAllBreakpointEvents);
	}

	private void SendAllBreakpointEvents()
	{
		// Send a breakpoint changed event with verified false for every breakpoint, so the IDE can mark the BP as unverified, until it receives our later BP events when we bind them
		foreach (var bp in _breakpointManager.GetAllBreakpoints())
		{
			OnBreakpointChanged?.Invoke(bp);
		}
	}

	private void Continue()
	{
		Guard.Against.Null(_process);
		_process.Continue(false);
	}

	private void ConfigureExceptionCallbacks()
	{
		if (_process is not ICorDebugProcess8 process8) return;
		var result = process8.TryEnableExceptionCallbacksOutsideOfMyCode(!_justMyCode);
		if (result is not Cor.S_OK) _logger?.Invoke($"Unable to configure exception callbacks outside user code: {result}");
	}

	private ICorDebugStepper? _stepper;

	/// <summary>
	/// Setup a stepper without continuing execution
	/// </summary>
	internal ICorDebugStepper SetupStepper(ICorDebugThread thread, AsyncStepper.StepType stepType)
	{
		var frame = thread.ActiveFrame;
		if (frame is not ICorDebugILFrame ilFrame) throw new InvalidOperationException("Active frame is not an IL frame");
		if (_stepper is not null) throw new InvalidOperationException("A step operation is already in progress");

		ICorDebugStepper stepper = frame.CreateStepper();
		stepper.SetInterceptMask(CorDebugIntercept.INTERCEPT_ALL & ~(CorDebugIntercept.INTERCEPT_SECURITY | CorDebugIntercept.INTERCEPT_CLASS_INIT));
		stepper.SetUnmappedStopMask(CorDebugUnmappedStop.STOP_NONE);
		if (_justMyCode) stepper.SetJMC(true);

		if (stepType == AsyncStepper.StepType.StepOut)
		{
			stepper.StepOut();
		}
		else // StepIn or StepOver
		{
			var metadataReader = _modules[frame.Function.Module.BaseAddress].MetadataReader;

			var currentIlOffset = ilFrame.IP.pnOffset;
			var nullableResult = metadataReader.GetStartAndEndSequencePointIlOffsetsForIlOffset(frame.Function.Token, currentIlOffset);
			if (nullableResult is var (startIlOffset, endIlOffset))
			{
				if (startIlOffset == endIlOffset)
				{
					endIlOffset = frame.Function.ILCode.Size;
				}
				var stepRange = new COR_DEBUG_STEP_RANGE
				{
					startOffset = checked((uint)startIlOffset),
					endOffset = checked((uint)endIlOffset)
				};
				var stepIn = stepType is AsyncStepper.StepType.StepIn;
				stepper.StepRange(stepIn, [stepRange], 1);
			}
			else
			{
				var stepIn = stepType is AsyncStepper.StepType.StepIn;
				stepper.Step(stepIn);
			}
		}

		_stepper = stepper;
		return stepper;
	}

	private ICorDebugStepper? _awaitingUserFrameStepper;

	/// <summary>
	/// Step out of every frame above the first user code frame on the stack, without Just My Code so the step stops exactly there
	/// </summary>
	private bool TrySetupStepOutToAwaitingUserFrame(ICorDebugThread thread)
	{
		if (_stepper is not null) throw new InvalidOperationException("A step operation is already in progress");
		ICorDebugILFrame? callee = null;
		foreach (var frame in EnumerateFramesForThread(thread))
		{
			if (frame is not ICorDebugILFrame ilFrame) continue;
			if (callee is not null && _modules.TryGetValue(ilFrame.Function.Module.BaseAddress, out var module) && module.IsUserCode && module.MetadataReader.HasSymbols)
			{
				var stepper = callee.CreateStepper();
				stepper.SetInterceptMask(CorDebugIntercept.INTERCEPT_ALL & ~(CorDebugIntercept.INTERCEPT_SECURITY | CorDebugIntercept.INTERCEPT_CLASS_INIT));
				stepper.SetUnmappedStopMask(CorDebugUnmappedStop.STOP_NONE);
				stepper.StepOut();
				_stepper = stepper;
				_awaitingUserFrameStepper = stepper;
				return true;
			}
			callee = ilFrame;
		}
		return false;
	}

	/// <summary>
	/// Step out of an async void method's state machine. Returns false when a plain step-out does the right thing
	/// </summary>
	/// <remarks>
	/// Before its first yield the step-out returns to the kick-off method's caller. Once the method has resumed after an await, the
	/// thread pool called it, and there is no task for anyone to await, so there is nothing to return to: the step runs free, as in
	/// vsdbg and ReSharper, rather than stopping in thread pool code or in whichever unrelated continuation that thread runs next
	/// </remarks>
	internal bool TrySetupAsyncVoidStepOut(ICorDebugThread thread, CORDB_ADDRESS moduleAddress, int kickoffMethodToken)
	{
		if (kickoffMethodToken is 0) return false;
		TrySetupStepOutBeforeFirstYield(thread, moduleAddress, kickoffMethodToken);
		return true;
	}

	private ICorDebugStepper? _stepOutToCallerStepper;

	/// <summary>
	/// Step out of an async state machine that has not yielded yet, i.e. whose kick-off method is still on the stack, to the kick-off
	/// method's caller: until its first yield an async method is an ordinary call. Returns false once the method has resumed after an await
	/// </summary>
	/// <remarks>
	/// The step leaves the kick-off method exactly, without Just My Code, so it does not stop in the builder's Start or in the kick-off
	/// method's hidden code. Once back in the caller: if the caller's statement goes on to await, the step finishes the statement (see
	/// <see cref="TryFinishAwaitingStatement"/>), so <c>await M()</c> lands after the await as when M has yielded, while
	/// <c>var task = M();</c> stops on that line; and with Just My Code, a caller that is not user code is stepped out of as usual
	/// </remarks>
	internal bool TrySetupStepOutBeforeFirstYield(ICorDebugThread thread, CORDB_ADDRESS moduleAddress, int kickoffMethodToken)
	{
		if (kickoffMethodToken is 0) return false;
		var kickoffFrame = FindKickoffFrame(thread, moduleAddress, kickoffMethodToken);
		if (kickoffFrame is null) return false;
		if (_stepper is not null) throw new InvalidOperationException("A step operation is already in progress");
		var stepper = kickoffFrame.CreateStepper();
		stepper.SetInterceptMask(CorDebugIntercept.INTERCEPT_ALL & ~(CorDebugIntercept.INTERCEPT_SECURITY | CorDebugIntercept.INTERCEPT_CLASS_INIT));
		stepper.SetUnmappedStopMask(CorDebugUnmappedStop.STOP_NONE);
		stepper.StepOut();
		_stepper = stepper;
		_stepOutToCallerStepper = stepper;
		return true;
	}

	private (ICorDebugFunctionBreakpoint Breakpoint, int ThreadId)? _stateMachineStartBreakpoint;

	/// <summary>
	/// Without Just My Code, a step into an async method that lands in its kick-off method runs to the start of the state machine's
	/// MoveNext instead of stepping on, then steps to its first line (<see cref="TrySetupStepToFirstLine"/>)
	/// </summary>
	/// <remarks>
	/// The kick-off method has no source, so the step would carry on into what it calls: the state machine's constructor (also without
	/// source), then Object..ctor and the builder's Create and Start, which do have decompiled source and would each end the step there,
	/// and stepping over Start steps over MoveNext. Running to MoveNext works whatever the builder is, and finds MoveNext from metadata,
	/// so it also works for a module without a PDB, which is decompiled once the step reaches it. With Just My Code the stepper skips all
	/// of that by itself
	/// </remarks>
	private bool TrySetupRunToStateMachineStart(ICorDebugThread thread, ICorDebugILFrame frame)
	{
		if (_justMyCode) return false;
		var function = frame.Function;
		if (_modules.TryGetValue(function.Module.BaseAddress, out var module) is false) return false;
		if (module.MetadataReader.GetAsyncStateMachineMoveNextMethod(function.Token) is not { } moveNextToken) return false;

		var breakpoint = function.Module.GetFunctionFromToken(moveNextToken).ILCode.CreateBreakpoint(0);
		breakpoint.Activate(true);
		_stateMachineStartBreakpoint = (breakpoint, thread.Id);
		return true;
	}

	/// <summary>
	/// At the start of MoveNext, before its state dispatch: step over that dispatch to the method's first line. Returns false when the
	/// step is already there
	/// </summary>
	private bool TrySetupStepToFirstLine(ICorDebugThread thread)
	{
		if (thread.ActiveFrame is not ICorDebugILFrame ilFrame) return false;
		// Decompiles the module first if it has no PDB
		GetSourceInfoAtFrame(ilFrame, _justMyCode is false);
		var function = ilFrame.Function;
		var metadataReader = _modules[function.Module.BaseAddress].MetadataReader;
		var ip = ilFrame.IP.pnOffset;
		if (metadataReader.GetNextUserCodeIlOffset(function.Token, ip) is not { } firstLineOffset)
		{
			// No source for MoveNext either: step on as from any other method without source
			SetupStepper(thread, AsyncStepper.StepType.StepIn);
			return true;
		}
		if (firstLineOffset == ip) return false;

		if (_stepper is not null) throw new InvalidOperationException("A step operation is already in progress");
		var stepper = ilFrame.CreateStepper();
		stepper.SetInterceptMask(CorDebugIntercept.INTERCEPT_ALL & ~(CorDebugIntercept.INTERCEPT_SECURITY | CorDebugIntercept.INTERCEPT_CLASS_INIT));
		stepper.SetUnmappedStopMask(CorDebugUnmappedStop.STOP_NONE);
		stepper.StepRange(false, [new COR_DEBUG_STEP_RANGE { startOffset = checked((uint)ip), endOffset = checked((uint)firstLineOffset) }], 1);
		_stepper = stepper;
		return true;
	}

	private void ClearStateMachineStartBreakpoint()
	{
		if (_stateMachineStartBreakpoint is not var (breakpoint, _)) return;
		_stateMachineStartBreakpoint = null;
		// Leave a user breakpoint that shares the same CLR breakpoint active
		if (_breakpointManager.FindByCorBreakpoint(breakpoint) is null) breakpoint.TryActivate(false);
	}

	private ICorDebugILFrame? FindKickoffFrame(ICorDebugThread thread, CORDB_ADDRESS moduleAddress, int kickoffMethodToken) =>
		EnumerateFramesForThread(thread).OfType<ICorDebugILFrame>().Skip(1)
			.FirstOrDefault(frame => frame.Function.Module.BaseAddress == moduleAddress && frame.Function.Token == kickoffMethodToken);

	/// <summary>
	/// Back in the caller of an async method that had not yielded: if the rest of the current statement awaits, step over it, crossing
	/// the await, rather than stopping part way through the statement
	/// </summary>
	private async Task<bool> TryFinishAwaitingStatement(ICorDebugThread thread)
	{
		if (thread.ActiveFrame is not ICorDebugILFrame ilFrame || _asyncStepper is null) return false;
		var function = ilFrame.Function;
		if (_modules.TryGetValue(function.Module.BaseAddress, out var module) is false) return false;
		var metadataReader = module.MetadataReader;
		var asyncInfo = metadataReader.GetAsyncMethodSteppingInfo(function.Token);
		if (asyncInfo is null) return false;
		var ip = ilFrame.IP.pnOffset;
		if (metadataReader.GetStartAndEndSequencePointIlOffsetsForIlOffset(function.Token, ip) is not var (_, statementEnd)) return false;
		if (asyncInfo.AwaitInfos.Any(info => info.YieldOffset >= ip && info.YieldOffset < statementEnd) is false) return false;

		var (handledByAsyncStepper, useSimpleStepper) = await _asyncStepper.TrySetupAsyncStep(thread, AsyncStepper.StepType.StepOver);
		if (handledByAsyncStepper is false || useSimpleStepper is not false) SetupStepper(thread, AsyncStepper.StepType.StepOver);
		return true;
	}

	/// <summary>
	/// Try to bind a breakpoint to the actual code using symbol information
	/// </summary>
	private bool TryBindBreakpoint(BreakpointManager.BreakpointInfo bp)
	{
		try
		{
			if (_process is null) return false;

			// Find the module whose symbols hold the document the path names. Only the best matches are tried, so that a breakpoint
			// is never bound to a same-named file elsewhere because the file it names has no code at the line
			ModuleInfo? targetModule = null;
			ModuleMetadataReader.ResolvedBreakpoint? resolved = null;

			var documentMatches = _modules.Values
				.Where(moduleInfo => moduleInfo.MetadataReader.HasSymbols)
				.Select(moduleInfo => (Module: moduleInfo, Document: moduleInfo.MetadataReader.FindDocument(bp.FilePath)))
				.Where(match => match.Document is not null)
				.Select(match => (match.Module, Document: match.Document!.Value))
				.ToList();
			var bestDocument = documentMatches.Count is 0 ? default : documentMatches.Select(match => match.Document).Aggregate((best, next) => next.IsBetterThan(best) ? next : best);
			foreach (var (moduleInfo, document) in documentMatches.Where(match => bestDocument.IsBetterThan(match.Document) is false))
			{
				resolved = moduleInfo.MetadataReader.ResolveBreakpoint(document, bp.Line, bp.Column);
				if (resolved is not null)
				{
					targetModule = moduleInfo;
					break;
				}
			}

			if (targetModule is null || resolved is null)
			{
				// No module found with symbols for this file
				bp.Verified = false;
				bp.Message = "The breakpoint will not currently be hit. No symbols have been loaded for this document.";
				_logger?.Invoke($"Breakpoint at {bp.FilePath}:{bp.Line} - no symbols found");
				return false;
			}

			// Get the function from the method token
			var function = targetModule.Module.GetFunctionFromToken(resolved.MethodToken);
			var ilCode = function.ILCode;

			// Create a breakpoint at the resolved IL offset. Breakpoints at the same location share one runtime breakpoint, so that
			// they are handled together and stop once
			var corBreakpoint = TryGetEntryBreakpoint(targetModule.BaseAddress, resolved.MethodToken, resolved.ILOffset)
				?? _breakpointManager.FindSourceCorBreakpointAt(targetModule.BaseAddress, resolved.MethodToken, resolved.ILOffset)
				?? ilCode.CreateBreakpoint(resolved.ILOffset);
			if (corBreakpoint != _entryBreakpoint?.CorBreakpoint) corBreakpoint.Activate(true);

			// Update breakpoint info
			bp.CorBreakpoint = corBreakpoint;
			bp.Verified = true;
			bp.Line = resolved.StartLine;
			bp.Column = resolved.StartColumn;
			bp.EndLine = resolved.EndLine;
			bp.EndColumn = resolved.EndColumn;
			bp.ResolvedBreakpointFromPdb = resolved;
			bp.ModuleBaseAddress = targetModule.BaseAddress;
			bp.Message = null;

			_logger?.Invoke($"Breakpoint bound at {bp.FilePath}:{bp.Line} -> resolved to line {resolved.StartLine}, IL offset {resolved.ILOffset} in method 0x{resolved.MethodToken:X}");
			return true;
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"Error binding breakpoint at {bp.FilePath}:{bp.Line}: {ex.Message}");
			bp.Verified = false;
			bp.Message = $"Error binding breakpoint: {ex.Message}";
			return false;
		}
	}

	/// <summary>
	/// Try to bind all pending breakpoints (called when a new module is loaded)
	/// </summary>
	private void TryBindPendingBreakpoints()
	{
		var pendingBreakpoints = _breakpointManager.GetPendingBreakpoints().Where(bp => !bp.IsFunctionBreakpoint);

		foreach (var bp in pendingBreakpoints)
		{
			if (TryBindBreakpoint(bp))
			{
				// Notify that the breakpoint changed (became verified)
				OnBreakpointChanged?.Invoke(bp);
			}
		}
	}

	/// <summary>
	/// Binds all matching functions in a module and returns true only when the BreakpointInfo becomes verified.
	/// </summary>
	private bool TryBindFunctionBreakpoint(BreakpointManager.BreakpointInfo bp, ModuleInfo module)
	{
		if (module.MetadataReader.HasSymbols is false || bp.FunctionName is null) return false;
		var wasVerified = bp.Verified;
		try
		{
			var pattern = FunctionBreakpointPattern.Parse(bp.FunctionName);
			foreach (var resolved in FunctionBreakpointMetadataResolver.Resolve(module.MetadataReader, pattern))
			{
				if (bp.FunctionBindings.Any(binding => binding.ModuleBaseAddress == module.BaseAddress && binding.MethodToken == resolved.MethodToken))
				{
					continue;
				}
				var function = module.Module.GetFunctionFromToken(resolved.MethodToken);
				var corBreakpoint = TryGetEntryBreakpoint(module.BaseAddress, resolved.MethodToken, resolved.Source.ILOffset)
					?? function.ILCode.CreateBreakpoint(resolved.Source.ILOffset);
				if (corBreakpoint != _entryBreakpoint?.CorBreakpoint) corBreakpoint.Activate(true);
				bp.FunctionBindings.Add(new BreakpointManager.FunctionBreakpointBinding(corBreakpoint, module.BaseAddress, resolved.MethodToken, resolved.Source));
				bp.Verified = true;
				bp.Message = null;
			}
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"Error binding function breakpoint '{bp.FunctionName}' in {module.ModuleName}: {ex.Message}");
			if (!bp.Verified) bp.Message = $"Error binding function breakpoint: {ex.Message}";
		}
		return wasVerified is false && bp.Verified;
	}

	private void EnsureNoProcessBeingDebugged()
	{
		if (_process is not null) throw new InvalidOperationException("A process is already being debugged, you must terminate/detach first.");
	}

	private ICorDebugFunctionBreakpoint? TryGetEntryBreakpoint(CORDB_ADDRESS moduleBaseAddress, int methodToken, int ilOffset)
	{
		return _entryBreakpoint is { } entry && entry.ModuleBaseAddress == moduleBaseAddress &&
			entry.MethodToken == methodToken && entry.IlOffset == ilOffset
			? entry.CorBreakpoint
			: null;
	}

	internal ICorDebugILFrame GetIlFrameForThreadIdAndStackDepth(ThreadId threadId, FrameStackDepth stackDepth)
	{
		var frame = GetFrameForThreadIdAndStackDepth(threadId, stackDepth);
		if (frame is not ICorDebugILFrame ilFrame) throw new InvalidOperationException("Frame is not an IL frame");
		return ilFrame;
	}

	internal ICorDebugFrame GetFrameForThreadIdAndStackDepth(ThreadId threadId, FrameStackDepth stackDepth)
	{
		// We need to re-obtain the frame in case it has been neutered
		var thread = _process!.GetThread(threadId.Value);
		var frame = EnumerateFramesForThread(thread).ElementAt(stackDepth.Value);
		return frame;
	}

	private static IEnumerable<ICorDebugFrame> EnumerateFramesForThread(ICorDebugThread thread)
	{
		foreach (var chain in thread.EnumerateChains())
		{
			if (chain.IsManaged is false) continue;
			foreach (var frame in chain.EnumerateFrames())
			{
				yield return frame;
			}
		}
	}

	internal IReadOnlyCollection<ModuleInfo> AllModules => _modules.Values;
	internal ModuleInfo GetModuleInfoForModule(ICorDebugModule module) => _modules[module.BaseAddress];

	internal ICorDebugValue? GetCurrentException(ThreadId threadId)
	{
		var thread = _process?.GetThread(threadId.Value);
		if (thread is null) return null;
		thread.TryGetCurrentException(out var currentException);
		return currentException;
	}

	// Not intended to implement IDisposable - it is intended that this is called via Disconnect()
	private void Dispose()
	{
		if (_process is null) return; // A client may call Terminate, then Disconnect, both of which call Dispose. Dispose only needs to be run once.

		// Dispose modules, which releases PDB files
		foreach (var moduleInfo in _modules.Values)
		{
			moduleInfo.Dispose();
		}
		_modules.Clear();

		// Deactivate all breakpoints
		if (_entryBreakpoint is { } entryBreakpoint)
		{
			entryBreakpoint.CorBreakpoint.TryActivate(false);
			_entryBreakpoint = null;
		}
		_stopAtEntry = false;
		foreach (var bp in _breakpointManager.GetAllBreakpoints().Where(b => (b.CorBreakpoint is not null && b.IsFunctionBreakpoint is false) || b.IsFunctionBreakpoint))
		{
			var corBreakpoints = bp.IsFunctionBreakpoint ? bp.FunctionBindings.Select(binding => binding.CorBreakpoint) : [bp.CorBreakpoint!];
			foreach (var corBreakpoint in corBreakpoints)
			{
				var hResult = corBreakpoint.TryActivate(false);
				if (hResult is Cor.CORDBG_E_PROCESS_TERMINATED) break;
				if (hResult is not Cor.S_OK) _logger?.Invoke($"Failed to deactivate breakpoint {bp.Id}: {hResult}");
			}
		}
		_breakpointManager.Clear();

		_asyncStepper?.Dispose();
		_asyncStepper = null;
		_stepper = null!;
		_threads.Clear();
		_mainThreadId = null;
		_oldestThreadId = null;
		_exceptionStates.Clear();
		_exceptionBreakModes.Clear();
		_initializedStaticTypes.Clear();
		_suppressFinalizeFunction = null;
		_variableManager.ClearAndTryDisposeHandleValues();
		_frameReferenceManager.Clear();

		// Unsubscribe from callbacks to avoid any further event dispatch
		_callbacks.OnAnyEvent -= QueueEvent;
		_runtimeEventChannel.Writer.Complete();
		// ProcessRuntimeEventQueue is blocked on DapRequestAndRuntimeEventLock (which we hold) and would
		// never complete if we waited on it here — that is the deadlock. Read and discard remaining events ourselves,
		// then let the processor exit once the lock is released.
		while (_runtimeEventChannel.Reader.TryRead(out _)) { }

		// Detach from the process
		_process?.TryDetach();

		_isAttached = false;
		_process = null;
		_corDebug = null;

		_debuggeeProcess?.Dispose();
		_debuggeeProcess = null;
	}

	private sealed class ExceptionPropagationState
	{
		// True once exception propagation has entered a JMC-marked frame. If the runtime later finds a
		// handler outside JMC code, the exception is user-unhandled.
		public required bool HasReachedUserCode { get; set; }

		// Prevents the first-chance and user-first-chance callbacks for the same exception from producing
		// duplicate "all exceptions" stops.
		public required bool AlwaysStopReported { get; set; }
	}
}

public class EvalStatus
{
	public bool IsRunning { get; set; }
}
