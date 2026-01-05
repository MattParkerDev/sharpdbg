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
    private bool _stopAtEntry;
    private bool _isAttached;
    private int? _pendingAttachProcessId;
    private AsyncStepper? _asyncStepper;

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

    public BreakpointManager BreakpointManager => _breakpointManager;
    public VariableManager VariableManager => _variableManager;
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
        _callbacks.OnCreateProcess += HandleProcessCreated;
        _callbacks.OnExitProcess += HandleProcessExited;
        _callbacks.OnCreateThread += HandleThreadCreated;
        _callbacks.OnExitThread += HandleThreadExited;
        _callbacks.OnLoadModule += HandleModuleLoaded;
        _callbacks.OnBreakpoint += HandleBreakpoint;
        _callbacks.OnStepComplete += HandleStepComplete;
        _callbacks.OnBreak += HandleBreak;
        _callbacks.OnException += HandleException;
        //_callbacks.OnAnyEvent += (s, e) => e.Controller.Continue(false);
    }

    private void OnAnyEvent(object? sender, CorDebugManagedCallbackEventArgs e)
    {
	    _logger?.Invoke($"Event: {e.GetType().Name}");
	    if (e is CreateAppDomainCorDebugManagedCallbackEventArgs or LoadAssemblyCorDebugManagedCallbackEventArgs or NameChangeCorDebugManagedCallbackEventArgs)
	    {
		    e.Controller.Continue(false);
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
	    var frame = thread.ActiveChain.Frames[stackDepth.Value];
	    if (frame is not CorDebugILFrame ilFrame) throw new InvalidOperationException("Frame is not an IL frame");
	    return ilFrame;
	}

	private CompiledExpressionInterpreter? _expressionInterpreter;

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

    // Event handlers
    private void HandleProcessCreated(object? sender, CreateProcessCorDebugManagedCallbackEventArgs createProcessCorDebugManagedCallbackEventArgs)
    {
        _logger?.Invoke("Process created event");
        _rawProcess = createProcessCorDebugManagedCallbackEventArgs.Process;
        if (_stopAtEntry)
        {
            OnStopped?.Invoke(0, "entry");
        }
        ContinueProcess();
    }

    private void HandleProcessExited(object? sender, ExitProcessCorDebugManagedCallbackEventArgs exitProcessCorDebugManagedCallbackEventArgs)
    {
	    _logger?.Invoke($"Process exited");
        IsRunning = false;
        OnExited?.Invoke();
        OnTerminated?.Invoke();
    }

    private void HandleThreadCreated(object? sender, CreateThreadCorDebugManagedCallbackEventArgs createThreadCorDebugManagedCallbackEventArgs)
    {
	    var corThread = createThreadCorDebugManagedCallbackEventArgs.Thread;
        _threads[corThread.Id] = corThread;
        OnThreadStarted?.Invoke(corThread.Id, $"Thread {corThread.Id}");
        ContinueProcess();
    }

    private void HandleThreadExited(object? sender, ExitThreadCorDebugManagedCallbackEventArgs exitThreadCorDebugManagedCallbackEventArgs)
    {
        var corThread = exitThreadCorDebugManagedCallbackEventArgs.Thread;
        _threads.Remove(corThread.Id);
        OnThreadExited?.Invoke(corThread.Id, $"Thread {corThread.Id}");
        ContinueProcess();
    }

    private void HandleModuleLoaded(object? sender, LoadModuleCorDebugManagedCallbackEventArgs loadModuleCorDebugManagedCallbackEventArgs)
    {
        var corModule = loadModuleCorDebugManagedCallbackEventArgs.Module;
        var modulePath = corModule.Name;
        var moduleName = Path.GetFileName(modulePath);
        var baseAddress = (long)corModule.BaseAddress;

        _logger?.Invoke($"Module loaded: {modulePath} at 0x{baseAddress:X}");

        // Try to load symbols for this module
        SymbolReader? symbolReader = null;
        try
        {
            symbolReader = SymbolReader.TryLoad(modulePath);
            if (symbolReader != null)
            {
                _logger?.Invoke($"  Symbols loaded for {moduleName}");
            }
            else
            {
                _logger?.Invoke($"  No symbols found for {moduleName}");
            }
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"  Error loading symbols for {moduleName}: {ex.Message}");
        }

        // Store module info
        var moduleInfo = new ModuleInfo(corModule, modulePath, symbolReader);
        _modules[baseAddress] = moduleInfo;

        if (moduleName is "System.Private.CoreLib.dll")
        {
	        // we need to map value classes to primitive types to allow evaluation to invoke methods on them
	        MapRuntimePrimitiveTypesToCorDebugClass(corModule);
	        // We can now initialize the expression interpreter, and assume that modules will be loaded before any stop event is allowed to be returned
	        var runtimeAssemblyPrimitiveTypeClasses = new RuntimeAssemblyPrimitiveTypeClasses(CorElementToValueClassMap, CorVoidClass, CorDecimalClass);
	        _expressionInterpreter = new CompiledExpressionInterpreter(runtimeAssemblyPrimitiveTypeClasses, _callbacks, this);
        }

        // Fire the module loaded event
        OnModuleLoaded?.Invoke(modulePath, Path.GetFileName(modulePath), modulePath);

        // Try to bind any pending breakpoints now that we have a new module with symbols
        if (symbolReader != null)
        {
            TryBindPendingBreakpoints();
        }

        ContinueProcess();
    }

    private async void HandleBreakpoint(object? sender, BreakpointCorDebugManagedCallbackEventArgs breakpointCorDebugManagedCallbackEventArgs)
    {
	    try
	    {
		    //System.Diagnostics.Debugger.Launch();
		    var breakpoint = breakpointCorDebugManagedCallbackEventArgs.Breakpoint;
		    ArgumentNullException.ThrowIfNull(breakpoint);

		    if (_stepper is not null)
		    {
			    _stepper.Deactivate();
			    _stepper = null;
		    }

		    if (breakpoint is not CorDebugFunctionBreakpoint functionBreakpoint)
		    {
			    _logger?.Invoke("Unknown breakpoint type hit");
			    ContinueProcess(); // may be incorrect
			    return;
		    }
		    var corThread = breakpointCorDebugManagedCallbackEventArgs.Thread;

		    // Check if async stepper handles this breakpoint
		    if (_asyncStepper != null)
		    {
			    var (asyncHandled, shouldStop) = await _asyncStepper.TryHandleBreakpoint(corThread, functionBreakpoint);
			    if (asyncHandled)
			    {
				    if (shouldStop is false)
				    {
					    Continue();
					    return;
				    }
				    IsRunning = false;
				    if (_stepper is not null)
				    {
					    _stepper.Deactivate();
					    _stepper = null;
				    }
				    var sourceInfo = GetSourceInfoAtFrame(corThread.ActiveFrame);
				    if (sourceInfo is null)
				    {
					    SetupStepper(corThread, AsyncStepper.StepType.StepOut);
					    Continue();
					    return;
				    }
			    }
		    }

		    var managedBreakpoint = _breakpointManager.FindByCorBreakpoint(functionBreakpoint.Raw);
		    ArgumentNullException.ThrowIfNull(managedBreakpoint);
		    IsRunning = false;
		    OnStopped2?.Invoke(corThread.Id, managedBreakpoint.FilePath, managedBreakpoint.Line, "breakpoint");
	    }
	    catch (Exception e)
	    {
		    throw; // TODO handle exception
	    }
    }

    private void HandleStepComplete(object? sender, StepCompleteCorDebugManagedCallbackEventArgs stepCompleteEventArgs)
    {
	    var corThread = stepCompleteEventArgs.Thread;
        IsRunning = false;
        var ilFrame = (CorDebugILFrame)corThread.ActiveFrame;
        // If we have an active async stepper, it means we would have a breakpoint set up for either yield or resume for the next await statement
        // We would then have done a regular step over/in/out to get to that breakpoint
        // Since the step has completed, it means we did not hit the breakpoint, so we can clear the active async step
        _asyncStepper?.ClearActiveAsyncStep();
        var stepper = _stepper ?? throw new InvalidOperationException("No stepper found for step complete");
		stepper.Deactivate(); // I really don't know if its necessary to deactivate the steppers once done
		_stepper = null;
		var symbolReader = _modules[ilFrame.Function.Module.BaseAddress].SymbolReader;
		if (symbolReader is null)
		{
			// We don't have symbols, but we're going to step in, in case this code calls user code that would be missed if we stepped out or over
			// Alternative is to use JMC true - we'll never stop in non-user code, so in theory symbolReader would never be null
			SetupStepper(corThread, AsyncStepper.StepType.StepIn);
			Continue();
			return;
		}
		var (currentIlOffset, nextUserCodeIlOffset) = symbolReader.GetFrameCurrentIlOffsetAndNextUserCodeIlOffset(ilFrame);
		if (stepCompleteEventArgs.Reason is CorDebugStepReason.STEP_CALL && currentIlOffset < nextUserCodeIlOffset)
		{
			SetupStepper(corThread, AsyncStepper.StepType.StepOver);
			Continue();
			return;
		}
		if (nextUserCodeIlOffset is null)
		{
			// Check attributes
			var metadataImport = ilFrame.Function.Module.GetMetaDataInterface().MetaDataImport;
			var mdMethodDef = ilFrame.Function.Token;
			var methodIsNotDebuggable = metadataImport.HasAnyAttribute(mdMethodDef, JmcConstants.JmcMethodAttributeNames);
			if (methodIsNotDebuggable)
			{
				SetupStepper(corThread, AsyncStepper.StepType.StepIn);
				Continue();
				return;
			}
		}
		var sourceInfo = GetSourceInfoAtFrame(ilFrame);
		if (sourceInfo is null)
		{
			// sourceInfo will be null if we could not find a PDB for the module
			// Bottom line - if we have no PDB, we have no source info, and there is no possible way for the user to map the stop location to a source file/line
			// (Until we implement Source Link and/or Decompilation support)
			// So for now, if this occurs, we are going to do a step out to get us back to a stop location with source info
			// TODO: This should probably be more sophisticated - mark the CorDebugFunction as non user code - `JMCStatus = false`, enable JMC for the stepper and then step over, in case the non user code calls user code, e.g. LINQ methods
			SetupStepper(corThread, AsyncStepper.StepType.StepOver);
			Continue();
			return;
		}
		var (sourceFilePath, line, _) = sourceInfo.Value;
		OnStopped2?.Invoke(corThread.Id, sourceFilePath, line, "step");
    }

    private void HandleBreak(object? sender, BreakCorDebugManagedCallbackEventArgs breakCorDebugManagedCallbackEventArgs)
    {
        var corThread = breakCorDebugManagedCallbackEventArgs.Thread;
        IsRunning = false;
        _asyncStepper?.Disable();
        if (_stepper is not null)
        {
	        _stepper.Deactivate();
	        _stepper = null;
        }
        OnStopped?.Invoke(corThread.Id, "pause");
    }

    private void HandleException(object? sender, ExceptionCorDebugManagedCallbackEventArgs exceptionCorDebugManagedCallbackEventArgs)
    {
	    var corThread = exceptionCorDebugManagedCallbackEventArgs.Thread;
        IsRunning = false;
        _asyncStepper?.Disable();
        if (_stepper is not null)
        {
	        _stepper.Deactivate();
	        _stepper = null;
        }
        OnStopped?.Invoke(corThread.Id, "exception");
    }

    public void Dispose()
    {
        Cleanup();
        _process = null;
        _corDebug = null;
        _asyncStepper?.Dispose();
        _asyncStepper = null;
    }
}
