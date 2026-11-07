using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Ardalis.GuardClauses;
using ICorDebugSharp;
using NeoSmart.AsyncLock;
using SharpDbg.Infrastructure.Debugger.ExpressionEvaluator.Cil;
using SharpDbg.Infrastructure.Debugger.Models;
using SharpDbg.Infrastructure.Debugger.Models.Response;
using SharpDbg.Infrastructure.Debugger.Breakpoints;
using SharpDbg.Infrastructure.Debugger.Stepping;

namespace SharpDbg.Infrastructure.Debugger;

// v1 of this class was AI generated, and could definitely do with some cleaning up
public partial class ManagedDebugger
{
	private SessionState _session = SessionState.NotStarted.Instance;
	// Null unless a process is being debugged
	private ICorDebugProcess? CorProcess => _session is SessionState.Debugging debugging ? debugging.Process : null;
	internal RunState _run = RunState.Running.Instance;
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
	private bool _justMyCode;
	private EntryBreakpointState _entryBreakpoint = EntryBreakpointState.NotRequested.Instance;
	private ICorDebugFunctionBreakpoint? EntryCorBreakpoint => (_entryBreakpoint as EntryBreakpointState.Bound)?.CorBreakpoint;

	// The active exception filters and optional type conditions supplied by the DAP client.
	private IReadOnlyList<SharpDbgExceptionBreakpointRequest> _exceptionBreakpoints = [];
	// Per-thread state retained between callbacks for the same exception propagation sequence.
	private readonly Dictionary<ThreadId, ExceptionPropagationState> _exceptionStates = [];
	// The mode that caused each thread's latest exception stop, used by the exceptionInfo response.
	private readonly Dictionary<ThreadId, SharpDbgExceptionBreakMode> _exceptionBreakModes = [];
	private readonly AsyncStepper _asyncStepper;
	private CilExpressionEvaluator _expressionEvaluator = null!;

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
	// hitMessage: a message that belongs only to the current hit, e.g. a condition that failed to evaluate, shown in place of the breakpoint's own
	public delegate void BreakpointChangedHandler(BreakpointInfo breakpoint, string? hitMessage = null);
	public event BreakpointChangedHandler? OnBreakpointChanged;
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
		EvalStatus = new EvalStatus(this);
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
		var runBeforeEvent = _run;
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
				default: CorProcess?.Continue(false); break;
			}
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"Error handling event {e.GetType().Name}: {ex}");
			// A stop reported while handling this event (each report is a new Stopped) keeps the debuggee stopped, even though it failed
			// after the report, e.g. in an event subscriber
			if (_run is RunState.Stopped && ReferenceEquals(_run, runBeforeEvent) is false) return;
			if (CorProcess is not null && CorProcess.TryIsRunning(out var isRunning) is Cor.S_OK && isRunning is false)
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
	/// Attach to a local process in the background, returning straight away
	/// </summary>
	/// <remarks>
	/// The process may have been started with DOTNET_DefaultDiagnosticPortSuspend, and its runtime only starts, and so can only be
	/// attached to, once something resumes it: the client, after ConfigurationDone returns, or ConfigurationDone itself for a launch in
	/// a terminal
	/// </remarks>
	private void StartAttach(int processId)
	{
		_logger?.Invoke($"Attaching to process: {processId}");
		_session = new SessionState.Attaching(processId);
		// FinishAttach takes DapRequestAndRuntimeEventLock once attaching via dbgshim is complete. AsyncLock stores its re-entrant lock
		// state in an AsyncLocal, and Task.Run copies AsyncLocals from the current thread to the task. SuppressFlow stops that copy, so
		// FinishAttach cannot re-entrantly enter the lock this request holds
		using (ExecutionContext.SuppressFlow())
		{
			_ = Task.Run(() => FinishAttach(processId));
		}
	}

	private async Task FinishAttach(int processId)
	{
		try
		{
			var corDebug = await ClrDebugExtensions.Automatic(processId);
			using (await DapRequestAndRuntimeEventLock.LockAsync())
			{
				if (_session is not SessionState.Attaching)
				{
					_logger?.Invoke($"The session ended before the attach to process {processId} completed");
					return;
				}
				AttachToProcess(corDebug, processId, debuggeeProcess: null);
			}
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"Error attaching to process {processId}: {ex}");
		}
	}

	/// <param name="debuggeeProcess">The process when the debugger started it</param>
	private void AttachToProcess(ICorDebug corDebug, int processId, Process? debuggeeProcess)
	{
		corDebug.Initialize();
		corDebug.SetManagedHandler(_callbacks);

		var process = corDebug.DebugActiveProcess(processId, false);
		_session = new SessionState.Debugging(corDebug, process, debuggeeProcess, IsRemote: false);
		ConfigureExceptionCallbacks();

		_logger?.Invoke($"Attached to process: {processId}");
		SendAllBreakpointEvents();
	}

	private void PerformRemoteAttach(RemoteAttachInfo remoteAttachInfo)
	{
		_logger?.Invoke($"Attaching to remote process on {remoteAttachInfo.Address}:{remoteAttachInfo.Port}");

		var corDebug = ClrDebugExtensions.Mobile(remoteAttachInfo);
		corDebug.SetManagedHandler(_callbacks);
		// It is expected that this does not return a ICorDebugProcess in the remote scenario - it is obtained via the CreateProcess callback instead
		_ = corDebug.TryDebugActiveProcess(0, false, out _);
		_session = new SessionState.WaitingForRemoteDebuggee(corDebug);

		_logger?.Invoke($"Debugger listening on port {remoteAttachInfo.Port}, awaiting connection from debuggee");
		SendAllBreakpointEvents();
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
		Guard.Against.Null(CorProcess);
		CorProcess.Continue(false);
	}

	/// <summary>
	/// Tell the client the debuggee has stopped. Every stop is reported through here
	/// </summary>
	/// <param name="location">Where the thread stopped, when known</param>
	private void ReportStop(int threadId, string reason, (string FilePath, int Line, int Column)? location = null, List<int>? hitBreakpointIds = null)
	{
		_run = new RunState.Stopped(threadId, reason);
		if (location is var (filePath, line, column)) OnStopped2?.Invoke(threadId, filePath, line, column, reason, hitBreakpointIds);
		else OnStopped?.Invoke(threadId, reason);
	}

	/// <summary>
	/// Tell the client the thread has stopped, at the location of its active frame
	/// </summary>
	private void ReportStopAtActiveFrame(ICorDebugThread thread, string reason, List<int>? hitBreakpointIds = null)
	{
		var sourceInfo = GetSourceInfoAtFrame(thread.ActiveFrame, _justMyCode is false);
		ReportStop(thread.Id, reason, sourceInfo is { } source ? (source.FilePath, source.StartLine, source.StartColumn) : null, sourceInfo is null ? null : hitBreakpointIds);
	}

	/// <summary>
	/// Continue from a stop reported to the client. The stop's variable and frame references are no longer valid
	/// </summary>
	/// <remarks>
	/// Tells the client, with a continued event sent before the request's response. Resuming from an event the
	/// client was never told about, e.g. a breakpoint whose condition is false, sends no continued event
	/// </remarks>
	/// <param name="threadId">The thread the client's request named</param>
	private void ContinueFromStop(int threadId)
	{
		if (_run is not RunState.Stopped) throw new InvalidOperationException("The debuggee is not stopped");
		Guard.Against.Null(CorProcess);
		_variableManager.ClearAndDisposeHandleValues();
		_frameReferenceManager.Clear();

		// The runtime counts stops, and only reports a superfluous continue when the count is zero, i.e. the process is already running.
		// The stop was undone without the client being told, e.g. by an evaluation that could not be aborted, so the debuggee is
		// running and the continue has nothing to do
		var result = CorProcess.TryContinue(false);
		if (result is Cor.CORDBG_E_SUPERFLOUS_CONTINUE) _logger?.Invoke("The debuggee was reported stopped, but was already running");
		else Marshal.ThrowExceptionForHR(result);
		_run = RunState.Running.Instance;
		// The request holds DapRequestAndRuntimeEventLock, so this is sent before any stop the continue leads to
		OnContinued?.Invoke(threadId);
	}

	private void ConfigureExceptionCallbacks()
	{
		if (CorProcess is not ICorDebugProcess8 process8) return;
		var result = process8.TryEnableExceptionCallbacksOutsideOfMyCode(!_justMyCode);
		if (result is not Cor.S_OK) _logger?.Invoke($"Unable to configure exception callbacks outside user code: {result}");
	}

	/// <summary>
	/// Try to bind a breakpoint to the actual code using symbol information
	/// </summary>
	private bool TryBindBreakpoint(BreakpointInfo bp)
	{
		try
		{
			if (CorProcess is null) return false;

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
				resolved = moduleInfo.MetadataReader.ResolveBreakpoint(document, bp.RequestedLine, bp.RequestedColumn);
				if (resolved is not null)
				{
					targetModule = moduleInfo;
					break;
				}
			}

			if (targetModule is null || resolved is null)
			{
				// No module found with symbols for this file
				bp.Binding = new BreakpointBinding.Unbound(NoSymbolsForDocumentMessage);
				_logger?.Invoke($"Breakpoint at {bp.FilePath}:{bp.RequestedLine} - no symbols found");
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
			if (corBreakpoint != EntryCorBreakpoint) corBreakpoint.Activate(true);

			bp.Binding = new BreakpointBinding.SourceBound(corBreakpoint, targetModule.BaseAddress, resolved);

			_logger?.Invoke($"Breakpoint bound at {bp.FilePath}:{bp.RequestedLine} -> resolved to line {resolved.StartLine}, IL offset {resolved.ILOffset} in method 0x{resolved.MethodToken:X}");
			return true;
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"Error binding breakpoint at {bp.FilePath}:{bp.RequestedLine}: {ex.Message}");
			bp.Binding = new BreakpointBinding.Unbound($"Error binding breakpoint: {ex.Message}");
			return false;
		}
	}

	private const string NoSymbolsForDocumentMessage = "The breakpoint will not currently be hit. No symbols have been loaded for this document.";
	private const string NoMatchingFunctionMessage = "The breakpoint will not currently be hit. No matching function has been loaded.";

	/// <summary>
	/// Try to bind all unbound source breakpoints (called when a new module is loaded)
	/// </summary>
	private void TryBindUnboundSourceBreakpoints()
	{
		foreach (var bp in _breakpointManager.GetUnboundSourceBreakpoints())
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
	private bool TryBindFunctionBreakpoint(BreakpointInfo bp, ModuleInfo module)
	{
		if (module.MetadataReader.HasSymbols is false || bp.FunctionName is null) return false;
		if (bp.Binding is BreakpointBinding.Rejected) return false;
		var wasVerified = bp.Verified;
		try
		{
			var pattern = FunctionBreakpointPattern.Parse(bp.FunctionName);
			foreach (var resolved in FunctionBreakpointMetadataResolver.Resolve(module.MetadataReader, pattern))
			{
				var bindings = bp.Binding is BreakpointBinding.FunctionBound bound ? bound.Bindings : [];
				if (bindings.Any(binding => binding.ModuleBaseAddress == module.BaseAddress && binding.MethodToken == resolved.MethodToken))
				{
					continue;
				}
				var function = module.Module.GetFunctionFromToken(resolved.MethodToken);
				var corBreakpoint = TryGetEntryBreakpoint(module.BaseAddress, resolved.MethodToken, resolved.Source.ILOffset)
					?? function.ILCode.CreateBreakpoint(resolved.Source.ILOffset);
				if (corBreakpoint != EntryCorBreakpoint) corBreakpoint.Activate(true);
				bp.Binding = new BreakpointBinding.FunctionBound(bindings.Add(new FunctionBreakpointBinding(corBreakpoint, module.BaseAddress, resolved.MethodToken, resolved.Source)));
			}
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"Error binding function breakpoint '{bp.FunctionName}' in {module.ModuleName}: {ex.Message}");
			if (bp.Binding is BreakpointBinding.Unbound) bp.Binding = new BreakpointBinding.Unbound($"Error binding function breakpoint: {ex.Message}");
		}
		return wasVerified is false && bp.Verified;
	}

	private void EnsureSessionNotStarted()
	{
		switch (_session)
		{
			case SessionState.NotStarted: return;
			case SessionState.LaunchRequested or SessionState.AttachRequested or SessionState.RemoteAttachRequested:
				throw new InvalidOperationException("A launch or attach has already been requested.");
			case SessionState.Attaching or SessionState.WaitingForRemoteDebuggee or SessionState.Debugging:
				throw new InvalidOperationException("A process is already being debugged.");
			case SessionState.Ended:
				throw new InvalidOperationException("The debug session has ended, so this debugger cannot debug another process.");
		}
	}

	private ICorDebugFunctionBreakpoint? TryGetEntryBreakpoint(CORDB_ADDRESS moduleBaseAddress, int methodToken, int ilOffset)
	{
		return _entryBreakpoint is EntryBreakpointState.Bound entry && entry.ModuleBaseAddress == moduleBaseAddress &&
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
		var thread = CorProcess!.GetThread(threadId.Value);
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
		var thread = CorProcess?.GetThread(threadId.Value);
		if (thread is null) return null;
		thread.TryGetCurrentException(out var currentException);
		return currentException;
	}

	// Not intended to implement IDisposable - it is intended that this is called via Disconnect()
	private void Dispose()
	{
		// A client may call Terminate, then Disconnect, both of which call Dispose. Dispose only needs to be run once.
		if (_session is not SessionState.Debugging debugging)
		{
			// An attach that completes, or a remote debuggee that connects, from now on is ignored
			if (_session is SessionState.Attaching or SessionState.WaitingForRemoteDebuggee) _session = SessionState.Ended.Instance;
			return;
		}

		// Dispose modules, which releases PDB files
		foreach (var moduleInfo in _modules.Values)
		{
			moduleInfo.Dispose();
		}
		_modules.Clear();

		// Deactivate all breakpoints
		EntryCorBreakpoint?.TryActivate(false);
		_entryBreakpoint = EntryBreakpointState.NotRequested.Instance;
		foreach (var bp in _breakpointManager.GetAllBreakpoints())
		{
			foreach (var corBreakpoint in bp.CorBreakpoints)
			{
				var hResult = corBreakpoint.TryActivate(false);
				if (hResult is Cor.CORDBG_E_PROCESS_TERMINATED) break;
				if (hResult is not Cor.S_OK) _logger?.Invoke($"Failed to deactivate breakpoint {bp.Id}: {hResult}");
			}
		}
		_breakpointManager.Clear();

		EndStep();
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
		debugging.Process.TryDetach();
		debugging.DebuggeeProcess?.Dispose();
		_session = SessionState.Ended.Instance;
	}

	/// <summary>
	/// An exception in flight on a thread, from its first-chance callback until a catch handler is found or it goes unhandled.
	/// A thread with no exception in flight has no entry in <see cref="_exceptionStates"/>
	/// </summary>
	/// <param name="HasReachedUserCode">
	/// True once the exception has entered a JMC-marked frame. If the runtime then finds a handler outside JMC code, the exception is user-unhandled.
	/// </param>
	/// <param name="AlwaysStopReported">
	/// True once an "all exceptions" stop has been reported, so the first-chance and user-first-chance callbacks don't both stop.
	/// </param>
	private readonly record struct ExceptionPropagationState(bool HasReachedUserCode, bool AlwaysStopReported)
	{
		/// <summary>The state to assume when a callback arrives for a thread with no exception in flight</summary>
		public static readonly ExceptionPropagationState NotStarted = new(HasReachedUserCode: false, AlwaysStopReported: false);
	}
}

/// <summary>
/// Lets code that runs an evaluation, which only has this and not the debugger, mark the debuggee as <see cref="RunState.Evaluating"/>
/// </summary>
public sealed class EvalStatus
{
	private readonly ManagedDebugger _debugger;
	internal EvalStatus(ManagedDebugger debugger) => _debugger = debugger;

	/// <summary>
	/// Mark the debuggee as <see cref="RunState.Evaluating"/>, nested in any evaluation already running, until the returned scope is disposed
	/// </summary>
	internal EvaluationScope BeginEvaluation()
	{
		_debugger._run = new RunState.Evaluating(_debugger._run);
		return new EvaluationScope(_debugger);
	}

	internal readonly struct EvaluationScope(ManagedDebugger debugger) : IDisposable
	{
		public void Dispose()
		{
			// Events raised by an evaluation continue rather than stop, and nested evaluations end before the one they are nested in
			if (debugger._run is not RunState.Evaluating evaluating) throw new UnreachableException($"Expected an evaluation to be running, but was {debugger._run}");
			debugger._run = evaluating.Interrupted;
		}
	}
}
