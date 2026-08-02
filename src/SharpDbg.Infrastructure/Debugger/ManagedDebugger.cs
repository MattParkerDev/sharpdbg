using System.Diagnostics;
using System.Runtime.InteropServices;
using Ardalis.GuardClauses;
using ICorDebugSharp;
using SharpDbg.Infrastructure.Debugger.ExpressionEvaluator;
using SharpDbg.Infrastructure.Debugger.ExpressionEvaluator.Compiler;
using SharpDbg.Infrastructure.Debugger.ExpressionEvaluator.Interpreter;
using SharpDbg.Infrastructure.Debugger.Models;
using ZLinq;

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
	private readonly Dictionary<int, ICorDebugThread> _threads = new();
	private readonly Dictionary<CORDB_ADDRESS, ModuleInfo> _modules = new();
	private bool _isAttached;
	private bool _isRemoteAttach;
	private int? _pendingAttachProcessId;
	private bool _justMyCode;
	private AsyncStepper? _asyncStepper;
	private CompiledExpressionInterpreter _expressionInterpreter = null!;

	private Process? _debuggeeProcess;

	public event Action<int, string>? OnStopped;
	// ThreadId, FilePath, Line, Column, Reason
	public event Action<int, string, int, int, string, DecompiledSourceInfo?>? OnStopped2;
	public event Action<int>? OnContinued;
	public event Action? OnExited;
	public event Action? OnTerminated;
	public event Action<int, string>? OnThreadStarted;
	public event Action<int, string>? OnThreadExited;
	public event Action<string, string, string>? OnModuleLoaded;
	// Output text, isError (true for stderr, false for stdout)
	public event Action<string, bool>? OnOutput;
	public event Action<BreakpointManager.BreakpointInfo>? OnBreakpointChanged;
	public event Func<LaunchInfo, int> SendRunInTerminalRequest = null!;

	public EvalStatus EvalStatus { get; }

	public ManagedDebugger(Action<string>? logger = null)
	{
		_logger = logger;
		_breakpointManager = new BreakpointManager();
		_variableManager = new VariableManager();
		_frameReferenceManager = new FrameReferenceManager();
		_callbacks = new CorDebugManagedCallback();
		EvalStatus = new EvalStatus();
		_asyncStepper = new AsyncStepper(_modules, _callbacks, this);

		// Subscribe to callback events
		_callbacks.OnAnyEvent += OnAnyEvent;
	}

	private async void OnAnyEvent(object? sender, CorDebugManagedCallbackEventArgs e)
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
				case BreakpointCorDebugManagedCallbackEventArgs a: await HandleBreakpoint(sender, a).ConfigureAwait(false); break;
				case StepCompleteCorDebugManagedCallbackEventArgs a: HandleStepComplete(sender, a); break;
				case BreakCorDebugManagedCallbackEventArgs a: HandleBreak(sender, a); break;
				case ExceptionCorDebugManagedCallbackEventArgs a: HandleException(sender, a); break;
				case EvalCompleteCorDebugManagedCallbackEventArgs or EvalExceptionCorDebugManagedCallbackEventArgs: break; // don't continue on these, as they are being used for expression evaluation
				default: _process?.Continue(false); break;
			}
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"Error handling event {e.GetType().Name}: {ex}");
			var isRunning = false;
			_process?.TryIsRunning(out isRunning);
			if (isRunning)
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
		//stepper.SetJMC(true);

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

	/// <summary>
	/// Try to bind a breakpoint to the actual code using symbol information
	/// </summary>
	private bool TryBindBreakpoint(BreakpointManager.BreakpointInfo bp)
	{
		try
		{
			if (_process is null) return false;

			// Find a module that contains the source file
			ModuleInfo? targetModule = null;
			ModuleMetadataReader.ResolvedBreakpoint? resolved = null;

			foreach (var moduleInfo in _modules.Values)
			{
				if (moduleInfo.MetadataReader.HasSymbols is false)
					continue;

				resolved = moduleInfo.MetadataReader.ResolveBreakpoint(bp.FilePath, bp.Line, bp.Column);
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

			// Create a breakpoint at the resolved IL offset
			var corBreakpoint = ilCode.CreateBreakpoint(resolved.ILOffset);
			corBreakpoint.Activate(true);

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
				var corBreakpoint = function.ILCode.CreateBreakpoint(resolved.Source.ILOffset);
				corBreakpoint.Activate(true);
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

	internal ICorDebugILFrame GetFrameForThreadIdAndStackDepth(ThreadId threadId, FrameStackDepth stackDepth)
	{
		// We need to re-obtain the IlFrame in case it has been neutered
		var thread = _process!.Threads.Single(s => s.Id == threadId.Value);
		var frame = thread.ActiveChain.Frames[stackDepth.Value];
		if (frame is not ICorDebugILFrame ilFrame) throw new InvalidOperationException("Frame is not an IL frame");
		return ilFrame;
	}

	private static string GetFunctionFormattedName(ICorDebugFunction function)
	{
		try
		{
			var token = function.Token;
			var module = function.Module;
			var metadataImport = module.GetMetaDataInterface<IMetaDataImport>();
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

	// Not intended to implement IDisposable - it is intended that this is called via Disconnect()
	private void Dispose()
	{
		// Dispose modules, which releases PDB files
		foreach (var moduleInfo in _modules.Values)
		{
			moduleInfo.Dispose();
		}
		_modules.Clear();

		// Deactivate all breakpoints
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
		_variableManager.ClearAndTryDisposeHandleValues();
		_frameReferenceManager.Clear();

		// Unsubscribe from callbacks to avoid any further event dispatch
		_callbacks.OnAnyEvent -= OnAnyEvent;

		// Detach from the process
		_process?.TryDetach();

		_isAttached = false;
		_process = null;
		_corDebug = null;

		_debuggeeProcess?.Dispose();
		_debuggeeProcess = null;
	}
}

public class EvalStatus
{
	public bool IsRunning { get; set; }
}
