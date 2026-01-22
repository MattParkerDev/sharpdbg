using System.Diagnostics;
using System.Runtime.InteropServices;
using Ardalis.GuardClauses;
using ClrDebug;
using SharpDbg.Infrastructure.Debugger.ExpressionEvaluator;
using SharpDbg.Infrastructure.Debugger.ExpressionEvaluator.Compiler;
using SharpDbg.Infrastructure.Debugger.ExpressionEvaluator.Interpreter;
using ZLinq;

namespace SharpDbg.Infrastructure.Debugger;

// v1 of this class was AI generated, and could definitely do with some cleaning up
public partial class ManagedDebugger : IDisposable
{
	private CorDebug? _corDebug;
	private CorDebugProcess? _process;
	private readonly CorDebugManagedCallback _callbacks;
	private readonly BreakpointManager _breakpointManager;
	private readonly VariableManager _variableManager;
	private readonly Action<string>? _logger;
	private readonly Dictionary<int, CorDebugThread> _threads = new();
	private readonly Dictionary<long, ModuleInfo> _modules = new();
	private bool _isAttached;
	private int? _pendingAttachProcessId;
	private AsyncStepper? _asyncStepper;
	private CompiledExpressionInterpreter? _expressionInterpreter;

	public event Action<int, string>? OnStopped;
	// ThreadId, FilePath, Line, Reason
	public event Action<int, string, int, string>? OnStopped2;
	public event Action<int>? OnContinued;
	public event Action? OnExited;
	public event Action? OnTerminated;
	public event Action<int, string>? OnThreadStarted;
	public event Action<int, string>? OnThreadExited;
	public event Action<string, string, string>? OnModuleLoaded;
	public event Action<string>? OnOutput;
	public event Action<BreakpointManager.BreakpointInfo>? OnBreakpointChanged;

	public bool IsRunning { get; private set; }
	private CorDebugProcess? _rawProcess;

	public ManagedDebugger(Action<string>? logger = null)
	{
		_logger = logger;
		_breakpointManager = new BreakpointManager();
		_variableManager = new VariableManager();
		_callbacks = new CorDebugManagedCallback();
		_asyncStepper = new AsyncStepper(_modules, _callbacks, this);

		// Subscribe to callback events
		_callbacks.OnAnyEvent += OnAnyEvent;
	}

	private void OnAnyEvent(object? sender, CorDebugManagedCallbackEventArgs e)
	{
		_logger?.Invoke($"Event: {e.GetType().Name}");
		switch (e)
		{
			case CreateProcessCorDebugManagedCallbackEventArgs a: HandleProcessCreated(sender, a); break;
			case ExitProcessCorDebugManagedCallbackEventArgs a: HandleProcessExited(sender, a); break;
			case CreateThreadCorDebugManagedCallbackEventArgs a: HandleThreadCreated(sender, a); break;
			case ExitThreadCorDebugManagedCallbackEventArgs a: HandleThreadExited(sender, a); break;
			case LoadModuleCorDebugManagedCallbackEventArgs a: HandleModuleLoaded(sender, a); break;
			case BreakpointCorDebugManagedCallbackEventArgs a: HandleBreakpoint(sender, a); break;
			case StepCompleteCorDebugManagedCallbackEventArgs a: HandleStepComplete(sender, a); break;
			case BreakCorDebugManagedCallbackEventArgs a: HandleBreak(sender, a); break;
			case ExceptionCorDebugManagedCallbackEventArgs a: HandleException(sender, a); break;
			case EvalCompleteCorDebugManagedCallbackEventArgs or EvalExceptionCorDebugManagedCallbackEventArgs: break; // don't continue on these, as they are being used for expression evaluation
			default: e.Controller.Continue(false); break;
		}
	}

	/// <summary>
	/// Actually attach to an existing process
	/// </summary>
	private void PerformAttach(int processId)
	{
		_logger?.Invoke($"Attaching to process: {processId}");

		// Initialize the debugger
		var dbgShimPath = DbgShimResolver.Resolve();
		var dbgshim = new DbgShim(NativeLibrary.Load(dbgShimPath));
		_ = Task.Run(() =>
		{
			_corDebug = ClrDebugExtensions.Automatic(dbgshim, processId);
			_corDebug.Initialize();
			_corDebug.SetManagedHandler(_callbacks);

			// Attach to the process
			_process = _corDebug.DebugActiveProcess(processId, false);
			_isAttached = true;
			IsRunning = true;

			_logger?.Invoke($"Attached to process: {processId}");
		});
	}

	private void ContinueProcess()
	{
		if (_rawProcess != null)
		{
			IsRunning = true;
			_rawProcess.Continue(false);
		}
	}

	private CorDebugStepper? _stepper;

	/// <summary>
	/// Setup a stepper without continuing execution
	/// </summary>
	internal CorDebugStepper SetupStepper(CorDebugThread thread, AsyncStepper.StepType stepType)
	{
		var frame = thread.ActiveFrame;
		if (frame is not CorDebugILFrame ilFrame) throw new InvalidOperationException("Active frame is not an IL frame");
		if (_stepper is not null) throw new InvalidOperationException("A step operation is already in progress");

		CorDebugStepper stepper = frame.CreateStepper();
		stepper.SetInterceptMask(CorDebugIntercept.INTERCEPT_ALL & ~(CorDebugIntercept.INTERCEPT_SECURITY | CorDebugIntercept.INTERCEPT_CLASS_INIT));
		stepper.SetUnmappedStopMask(CorDebugUnmappedStop.STOP_NONE);
		//stepper.SetJMC(true);

		if (stepType == AsyncStepper.StepType.StepOut)
		{
			stepper.StepOut();
		}
		else // StepIn or StepOver
		{
			var symbolReader = _modules[frame.Function.Module.BaseAddress].SymbolReader;

			var currentIlOffset = ilFrame.IP.pnOffset;
			var nullableResult = symbolReader?.GetStartAndEndSequencePointIlOffsetsForIlOffset(frame.Function.Token, currentIlOffset);
			if (nullableResult is var (startIlOffset, endIlOffset))
			{
				if (startIlOffset == endIlOffset)
				{
					endIlOffset = frame.Function.ILCode.Size;
				}
				var stepRange = new COR_DEBUG_STEP_RANGE
				{
					startOffset = startIlOffset,
					endOffset = endIlOffset
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

	/// <summary>
	/// Try to bind a breakpoint to the actual code using symbol information
	/// </summary>
	private bool TryBindBreakpoint(BreakpointManager.BreakpointInfo bp)
	{
		try
		{
			if (_process == null) return false;

			// Find a module that contains the source file
			ModuleInfo? targetModule = null;
			SymbolReader.ResolvedBreakpoint? resolved = null;

			foreach (var moduleInfo in _modules.Values)
			{
				if (moduleInfo.SymbolReader == null)
					continue;

				resolved = moduleInfo.SymbolReader.ResolveBreakpoint(bp.FilePath, bp.Line);
				if (resolved != null)
				{
					targetModule = moduleInfo;
					break;
				}
			}

			if (targetModule == null || resolved == null)
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

			// Create a breakpoint at the resolved IL offset
			var corBreakpoint = ilCode.CreateBreakpoint(resolved.ILOffset);
			corBreakpoint.Activate(true);

			// Update breakpoint info
			bp.CorBreakpoint = corBreakpoint;
			bp.Verified = true;
			bp.ResolvedLine = resolved.StartLine;
			bp.ResolvedEndLine = resolved.EndLine;
			bp.MethodToken = resolved.MethodToken;
			bp.ILOffset = resolved.ILOffset;
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
		var pendingBreakpoints = _breakpointManager.GetPendingBreakpoints();

		foreach (var bp in pendingBreakpoints)
		{
			if (TryBindBreakpoint(bp))
			{
				// Notify that the breakpoint changed (became verified)
				OnBreakpointChanged?.Invoke(bp);
			}
		}
	}

	internal CorDebugILFrame GetFrameForThreadIdAndStackDepth(ThreadId threadId, FrameStackDepth stackDepth)
	{
		// We need to re-obtain the IlFrame in case it has been neutered
		var thread = _process!.Threads.Single(s => s.Id == threadId.Value);
		int desired = stackDepth.Value;
		int absoluteIndex = 0;
		foreach (var chain in thread.EnumerateChains())
		{
			var frames = chain.Frames;
			for (int i = 0; i < frames.Length; i++)
			{
				if (absoluteIndex == desired)
				{
					var frame = frames[i];
					if (frame is not CorDebugILFrame ilFrame) throw new InvalidOperationException("Frame is not an IL frame");
					return ilFrame;
				}
				absoluteIndex++;
			}
		}
		throw new InvalidOperationException("Frame not found for given stack depth");
	}

	private void Cleanup()
	{
		_asyncStepper?.Disable();
		_threads.Clear();
		_breakpointManager.Clear();
		_variableManager.ClearAndDisposeHandleValues();

		// Dispose all module info (which disposes symbol readers)
		foreach (var moduleInfo in _modules.Values)
		{
			moduleInfo.Dispose();
		}
		_modules.Clear();

		_isAttached = false;
		IsRunning = false;
	}

	private static string GetFunctionFormattedName(CorDebugFunction function)
	{
		try
		{
			var token = function.Token;
			var module = function.Module;
			var metadataImport = module.GetMetaDataInterface().MetaDataImport;
			var methodName = metadataImport.GetMethodProps(token).szMethod;

			var @class = function.Class;
			var classToken = @class.Token;
			var className = metadataImport.GetTypeDefProps(classToken).szTypeDef;

			return $"{Path.GetFileName(module.Name)}!{className}.{methodName}()";
		}
		catch
		{
			return "Unknown";
		}
	}

	public void Dispose()
	{
		// Attempt a graceful disconnect from the debuggee without terminating it
		Disconnect(terminateDebuggee: false);

		// Remove our managed handler from ICorDebug so native code can release references
		_corDebug?.SetManagedHandler(null);

		// Unsubscribe from callbacks to avoid any further event dispatch
		_callbacks.OnAnyEvent -= OnAnyEvent;

		Cleanup();
		_process = null;
		_corDebug = null;
		_asyncStepper?.Dispose();
		_asyncStepper = null;
	}
}
