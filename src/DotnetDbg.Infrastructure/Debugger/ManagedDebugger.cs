using System.Diagnostics;
using System.Runtime.InteropServices;
using ClrDebug;

namespace DotnetDbg.Infrastructure.Debugger;

/// <summary>
/// Main debugger class wrapping ClrDebug functionality
/// </summary>
public class ManagedDebugger : IDisposable
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
    /// Launch a process to debug
    /// </summary>
    public void Launch(string program, string[] args, string? workingDirectory, Dictionary<string, string>? env, bool stopAtEntry)
    {
        _logger?.Invoke($"Launching: {program}");
        _stopAtEntry = stopAtEntry;

        // Initialize the debugger
        _corDebug = new CorDebug();
        _corDebug.Initialize();
        _corDebug.SetManagedHandler(_callbacks);

        workingDirectory ??= Path.GetDirectoryName(program) ?? Environment.CurrentDirectory;

        // Create and start the process using Process.Start for simplicity
        // Then attach the debugger
        var psi = new ProcessStartInfo
        {
            FileName = program,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false
        };

        // Use ArgumentList for safe argument passing (no injection risk)
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        if (env != null)
        {
            foreach (var kvp in env)
            {
                psi.Environment[kvp.Key] = kvp.Value;
            }
        }

        var systemProcess = Process.Start(psi);
        if (systemProcess == null)
        {
            throw new Exception("Failed to start process");
        }

        // Attach debugger to the started process
        _process = _corDebug.DebugActiveProcess(systemProcess.Id, false);
        _rawProcess = _process;
        _isAttached = true;
        IsRunning = !_stopAtEntry;
        _logger?.Invoke($"Process created and attached with PID: {systemProcess.Id}");
    }

    /// <summary>
    /// Store process ID for later attach (actual attach happens in ConfigurationDone)
    /// </summary>
    public void Attach(int processId)
    {
        _logger?.Invoke($"Storing attach target: {processId}");
        _pendingAttachProcessId = processId;
    }

    /// <summary>
    /// Called when DAP configuration is complete - performs deferred attach
    /// </summary>
    public void ConfigurationDone()
    {
	    //System.Diagnostics.Debugger.Launch();
        _logger?.Invoke("ConfigurationDone");

        if (_pendingAttachProcessId.HasValue)
        {
            PerformAttach(_pendingAttachProcessId.Value);
            _pendingAttachProcessId = null;
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

    /// <summary>
    /// Continue execution
    /// </summary>
    public void Continue()
    {
        _logger?.Invoke("Continue");
        if (_rawProcess != null)
        {
            IsRunning = true;
            _rawProcess.Continue(false);
        }
    }

    /// <summary>
    /// Pause execution
    /// </summary>
    public void Pause()
    {
        _logger?.Invoke("Pause");
        if (_rawProcess != null && IsRunning)
        {
            _rawProcess.Stop(0);
            IsRunning = false;
        }
    }

    /// <summary>
    /// Step to the next line
    /// </summary>
    public void StepNext(int threadId)
    {
        _logger?.Invoke($"StepNext on thread {threadId}");
        if (_threads.TryGetValue(threadId, out var thread))
        {
            var frame = thread.ActiveFrame;
            if (frame != null)
            {
                var stepper = frame.CreateStepper();
                stepper.StepRange(false, new COR_DEBUG_STEP_RANGE[0], 0);
                IsRunning = true;
                _rawProcess?.Continue(false);
            }
        }
    }

    /// <summary>
    /// Step into
    /// </summary>
    public void StepIn(int threadId)
    {
        _logger?.Invoke($"StepIn on thread {threadId}");
        if (_threads.TryGetValue(threadId, out var thread))
        {
            var frame = thread.ActiveFrame;
            if (frame != null)
            {
                var stepper = frame.CreateStepper();
                stepper.Step(true);
                IsRunning = true;
                _rawProcess?.Continue(false);
            }
        }
    }

    /// <summary>
    /// Step out
    /// </summary>
    public void StepOut(int threadId)
    {
        _logger?.Invoke($"StepOut on thread {threadId}");
        if (_threads.TryGetValue(threadId, out var thread))
        {
            var frame = thread.ActiveFrame;
            if (frame != null)
            {
                var stepper = frame.CreateStepper();
                stepper.StepOut();
                IsRunning = true;
                _rawProcess?.Continue(false);
            }
        }
    }

    /// <summary>
    /// Set breakpoints for a source file
    /// </summary>
    public List<BreakpointManager.BreakpointInfo> SetBreakpoints(string filePath, int[] lines)
    {
	    //System.Diagnostics.Debugger.Launch();
        _logger?.Invoke($"SetBreakpoints: {filePath}, lines: {string.Join(",", lines)}");

        // Deactivate and clear existing breakpoints for this file
        var existingBreakpoints = _breakpointManager.GetBreakpointsForFile(filePath);
        foreach (var bp in existingBreakpoints)
        {
            if (bp.CorBreakpoint != null)
            {
                try
                {
                    bp.CorBreakpoint.Activate(false);
                }
                catch (Exception ex)
                {
                    _logger?.Invoke($"Error deactivating breakpoint: {ex.Message}");
                }
            }
        }
        _breakpointManager.ClearBreakpointsForFile(filePath);

        // Create new breakpoints
        var result = new List<BreakpointManager.BreakpointInfo>();
        foreach (var line in lines)
        {
            var bp = _breakpointManager.CreateBreakpoint(filePath, line);

            // Try to bind the breakpoint if we have a process
            if (_process != null)
            {
                TryBindBreakpoint(bp);
            }
            else
            {
                // No process yet, mark as pending
                bp.Message = "The breakpoint is pending and will be resolved when debugging starts.";
            }

            result.Add(bp);
        }

        return result;
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

    /// <summary>
    /// Get all threads
    /// </summary>
    public List<(int id, string name)> GetThreads()
    {
        var result = new List<(int, string)>();
        if (_process == null) return result;

        try
        {
            var threads = _process.EnumerateThreads();
            foreach (var thread in threads)
            {
                result.Add((thread.Id, $"Thread {thread.Id}"));
            }
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"Error getting threads: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Get stack trace for a thread
    /// </summary>
    public List<StackFrameInfo> GetStackTrace(int threadId, int startFrame = 0, int? levels = null)
    {
        var result = new List<StackFrameInfo>();

        if (!_threads.TryGetValue(threadId, out var thread))
        {
            return result;
        }

        try
        {
            var chains = thread.EnumerateChains();
            foreach (var chain in chains)
            {
                var frames = chain.EnumerateFrames();
                var frameList = frames.ToList();

                var endFrame = levels.HasValue ? Math.Min(startFrame + levels.Value, frameList.Count) : frameList.Count;

                for (int i = startFrame; i < endFrame; i++)
                {
                    var frame = frameList[i];
                    if (frame is CorDebugILFrame ilFrame)
                    {
                        var function = ilFrame.Function;

                        var frameId = _variableManager.CreateReference(ilFrame);

                        result.Add(new StackFrameInfo
                        {
                            Id = frameId,
                            Name = GetFunctionName(function),
                            Line = 0, // Would need symbol info
                            Column = 0,
                            Source = null
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"Error getting stack trace: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Get scopes for a stack frame
    /// </summary>
    public List<ScopeInfo> GetScopes(int frameId)
    {
        var result = new List<ScopeInfo>();

        var frame = _variableManager.GetReference<CorDebugILFrame>(frameId);
        if (frame == null) return result;

        try
        {
            // Add locals scope
            var localsRef = _variableManager.CreateReference(new LocalsScope { Frame = frame });
            result.Add(new ScopeInfo
            {
                Name = "Locals",
                VariablesReference = localsRef,
                Expensive = false
            });

            // Add arguments scope
            var argsRef = _variableManager.CreateReference(new ArgumentsScope { Frame = frame });
            result.Add(new ScopeInfo
            {
                Name = "Arguments",
                VariablesReference = argsRef,
                Expensive = false
            });
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"Error getting scopes: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Get variables for a scope
    /// </summary>
    public List<VariableInfo> GetVariables(int variablesReference)
    {
        var result = new List<VariableInfo>();

        var scope = _variableManager.GetReference<object>(variablesReference);
        if (scope == null) return result;

        try
        {
            if (scope is LocalsScope localsScope)
            {
                // Get local variables - simplified, needs proper implementation
                result.Add(new VariableInfo
                {
                    Name = "local1",
                    Value = "value1",
                    Type = "int",
                    VariablesReference = 0
                });
            }
            else if (scope is ArgumentsScope argsScope)
            {
                // Get arguments - simplified, needs proper implementation
                result.Add(new VariableInfo
                {
                    Name = "arg1",
                    Value = "argValue",
                    Type = "string",
                    VariablesReference = 0
                });
            }
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"Error getting variables: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Evaluate an expression
    /// </summary>
    public (string result, string? type, int variablesReference) Evaluate(string expression, int? frameId)
    {
        _logger?.Invoke($"Evaluate: {expression}");

        // Simplified - proper implementation would use ICorDebugEval
        return ($"Evaluation not yet implemented: {expression}", "string", 0);
    }

    /// <summary>
    /// Terminate the debugged process
    /// </summary>
    public void Terminate()
    {
        _logger?.Invoke("Terminate");
        if (_process != null)
        {
            try
            {
                _process.Terminate(0);
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"Error terminating process: {ex.Message}");
            }
        }
        Cleanup();
    }

    /// <summary>
    /// Disconnect from the debuggee
    /// </summary>
    public void Disconnect(bool terminateDebuggee)
    {
        _logger?.Invoke($"Disconnect (terminate: {terminateDebuggee})");

        if (terminateDebuggee)
        {
            Terminate();
        }
        else
        {
            if (_process != null && _isAttached)
            {
                try
                {
                    _process.Detach();
                }
                catch (Exception ex)
                {
                    _logger?.Invoke($"Error detaching: {ex.Message}");
                }
            }
            Cleanup();
        }
    }

    private void Cleanup()
    {
        _threads.Clear();
        _breakpointManager.Clear();
        _variableManager.Clear();

        // Dispose all module info (which disposes symbol readers)
        foreach (var moduleInfo in _modules.Values)
        {
            moduleInfo.Dispose();
        }
        _modules.Clear();

        _isAttached = false;
        IsRunning = false;
    }

    private string GetFunctionName(CorDebugFunction function)
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
        Continue();
    }

    private void HandleProcessExited(object? sender, ExitProcessCorDebugManagedCallbackEventArgs exitProcessCorDebugManagedCallbackEventArgs)
    {
	    _logger?.Invoke($"Process exited");
        IsRunning = false;
        OnExited?.Invoke();
        OnTerminated?.Invoke();
        Continue();
    }

    private void HandleThreadCreated(object? sender, CreateThreadCorDebugManagedCallbackEventArgs createThreadCorDebugManagedCallbackEventArgs)
    {
	    var corThread = createThreadCorDebugManagedCallbackEventArgs.Thread;
        _threads[corThread.Id] = corThread;
        OnThreadStarted?.Invoke(corThread.Id, $"Thread {corThread.Id}");
        Continue();
    }

    private void HandleThreadExited(object? sender, ExitThreadCorDebugManagedCallbackEventArgs exitThreadCorDebugManagedCallbackEventArgs)
    {
        var corThread = exitThreadCorDebugManagedCallbackEventArgs.Thread;
        _threads.Remove(corThread.Id);
        OnThreadExited?.Invoke(corThread.Id, $"Thread {corThread.Id}");
        Continue();
    }

    private void HandleModuleLoaded(object? sender, LoadModuleCorDebugManagedCallbackEventArgs loadModuleCorDebugManagedCallbackEventArgs)
    {
        var corModule = loadModuleCorDebugManagedCallbackEventArgs.Module;
        var modulePath = corModule.Name;
        var baseAddress = (long)corModule.BaseAddress;

        _logger?.Invoke($"Module loaded: {modulePath} at 0x{baseAddress:X}");

        // Try to load symbols for this module
        SymbolReader? symbolReader = null;
        try
        {
            symbolReader = SymbolReader.TryLoad(modulePath);
            if (symbolReader != null)
            {
                _logger?.Invoke($"  Symbols loaded for {Path.GetFileName(modulePath)}");
            }
            else
            {
                _logger?.Invoke($"  No symbols found for {Path.GetFileName(modulePath)}");
            }
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"  Error loading symbols for {Path.GetFileName(modulePath)}: {ex.Message}");
        }

        // Store module info
        var moduleInfo = new ModuleInfo(corModule, modulePath, symbolReader);
        _modules[baseAddress] = moduleInfo;

        // Fire the module loaded event
        OnModuleLoaded?.Invoke(modulePath, Path.GetFileName(modulePath), modulePath);

        // Try to bind any pending breakpoints now that we have a new module with symbols
        if (symbolReader != null)
        {
            TryBindPendingBreakpoints();
        }

        Continue();
    }

    private void HandleBreakpoint(object? sender, BreakpointCorDebugManagedCallbackEventArgs breakpointCorDebugManagedCallbackEventArgs)
    {
	    //System.Diagnostics.Debugger.Launch();
	    var breakpoint = breakpointCorDebugManagedCallbackEventArgs.Breakpoint;
	    ArgumentNullException.ThrowIfNull(breakpoint);
	    if (breakpoint is not CorDebugFunctionBreakpoint functionBreakpoint)
	    {
		    Continue(); // may be incorrect
		    return;
	    }
	    var managedBreakpoint = _breakpointManager.FindByCorBreakpoint(functionBreakpoint.Raw);
	    ArgumentNullException.ThrowIfNull(managedBreakpoint);
	    var corThread = breakpointCorDebugManagedCallbackEventArgs.Thread;
        IsRunning = false;
        OnStopped2?.Invoke(corThread.Id, managedBreakpoint.FilePath, managedBreakpoint.Line, "breakpoint");
    }

    private void HandleStepComplete(object? sender, StepCompleteCorDebugManagedCallbackEventArgs stepCompleteCorDebugManagedCallbackEventArgs)
    {
	    var corThread = stepCompleteCorDebugManagedCallbackEventArgs.Thread;
        IsRunning = false;
        OnStopped?.Invoke(corThread.Id, "step");
    }

    private void HandleBreak(object? sender, BreakCorDebugManagedCallbackEventArgs breakCorDebugManagedCallbackEventArgs)
    {
        var corThread = breakCorDebugManagedCallbackEventArgs.Thread;
        IsRunning = false;
        OnStopped?.Invoke(corThread.Id, "pause");
    }

    private void HandleException(object? sender, ExceptionCorDebugManagedCallbackEventArgs exceptionCorDebugManagedCallbackEventArgs)
    {
	    var corThread = exceptionCorDebugManagedCallbackEventArgs.Thread;
        IsRunning = false;
        OnStopped?.Invoke(corThread.Id, "exception");
    }

    public void Dispose()
    {
        Cleanup();
        _process = null;
        _corDebug = null;
    }

    // Helper classes for scope tracking
    private class LocalsScope
    {
        public CorDebugILFrame? Frame { get; set; }
    }

    private class ArgumentsScope
    {
        public CorDebugILFrame? Frame { get; set; }
    }
}

// Helper classes for returning data
public class StackFrameInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Line { get; set; }
    public int Column { get; set; }
    public string? Source { get; set; }
}

public class ScopeInfo
{
    public string Name { get; set; } = string.Empty;
    public int VariablesReference { get; set; }
    public bool Expensive { get; set; }
}

public class VariableInfo
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string? Type { get; set; }
    public int VariablesReference { get; set; }
}
