using System.Diagnostics;
using ClrDebug;

namespace DotnetDbg.Infrastructure.Debugger;

/// <summary>
/// Main debugger class wrapping ClrDebug functionality
/// </summary>
public class ManagedDebugger : IDisposable
{
    private CorDebug? _corDebug;
    private CorDebugProcess? _process;
    private readonly DebuggerCallbacks _callbacks;
    private readonly BreakpointManager _breakpointManager;
    private readonly VariableManager _variableManager;
    private readonly Action<string>? _logger;
    private readonly Dictionary<int, CorDebugThread> _threads = new();
    private bool _stopAtEntry;
    private bool _isAttached;

    public event Action<int, string>? OnStopped;
    public event Action<int>? OnContinued;
    public event Action<int>? OnExited;
    public event Action? OnTerminated;
    public event Action<int, string>? OnThreadStarted;
    public event Action<int, string>? OnThreadExited;
    public event Action<string, string, string>? OnModuleLoaded;
    public event Action<string>? OnOutput;

    public BreakpointManager BreakpointManager => _breakpointManager;
    public VariableManager VariableManager => _variableManager;
    public bool IsRunning { get; private set; }
    private ICorDebugProcess? _rawProcess;

    public ManagedDebugger(Action<string>? logger = null)
    {
        _logger = logger;
        _breakpointManager = new BreakpointManager();
        _variableManager = new VariableManager();
        _callbacks = new DebuggerCallbacks(logger);

        // Subscribe to callback events
        _callbacks.OnProcessCreated += HandleProcessCreated;
        _callbacks.OnProcessExited += HandleProcessExited;
        _callbacks.OnThreadCreated += HandleThreadCreated;
        _callbacks.OnThreadExited += HandleThreadExited;
        _callbacks.OnModuleLoaded += HandleModuleLoaded;
        _callbacks.OnBreakpoint += HandleBreakpoint;
        _callbacks.OnStepComplete += HandleStepComplete;
        _callbacks.OnBreak += HandleBreak;
        _callbacks.OnException += HandleException;
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
        _rawProcess = _process.Raw;
        _isAttached = true;
        IsRunning = !_stopAtEntry;
        _logger?.Invoke($"Process created and attached with PID: {systemProcess.Id}");
    }

    /// <summary>
    /// Attach to an existing process
    /// </summary>
    public void Attach(int processId)
    {
        _logger?.Invoke($"Attaching to process: {processId}");

        // Initialize the debugger
        _corDebug = new CorDebug();
        _corDebug.Initialize();
        _corDebug.SetManagedHandler(_callbacks);

        // Attach to the process
        _process = _corDebug.DebugActiveProcess(processId, false);
        _isAttached = true;
        IsRunning = true;
        
        _logger?.Invoke($"Attached to process: {processId}");
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
        _logger?.Invoke($"SetBreakpoints: {filePath}, lines: {string.Join(",", lines)}");

        // Clear existing breakpoints for this file
        var existingBreakpoints = _breakpointManager.GetBreakpointsForFile(filePath);
        foreach (var bp in existingBreakpoints)
        {
            bp.CorBreakpoint?.Activate(false);
        }
        _breakpointManager.ClearBreakpointsForFile(filePath);

        // Create new breakpoints
        var result = new List<BreakpointManager.BreakpointInfo>();
        foreach (var line in lines)
        {
            var bp = _breakpointManager.CreateBreakpoint(filePath, line);
            
            // Try to bind the breakpoint
            if (_process != null)
            {
                TryBindBreakpoint(bp);
            }
            
            result.Add(bp);
        }

        return result;
    }

    /// <summary>
    /// Try to bind a breakpoint to the actual code
    /// </summary>
    private void TryBindBreakpoint(BreakpointManager.BreakpointInfo bp)
    {
        try
        {
            if (_process == null) return;

            // Find the function at this location
            // This is simplified - a real implementation would need symbol information
            // to map file/line to actual IL offset in a function
            
            var appDomains = _process.EnumerateAppDomains();
            foreach (var appDomain in appDomains)
            {
                var assemblies = appDomain.EnumerateAssemblies();
                foreach (var assembly in assemblies)
                {
                    var modules = assembly.EnumerateModules();
                    foreach (var module in modules)
                    {
                        // Try to get symbol reader for the module
                        // This would require PDB files and proper symbol resolution
                        // For now, mark as unverified
                    }
                }
            }

            // For now, mark as unverified - proper implementation needs symbol support
            _breakpointManager.SetVerified(bp.Id, false, "Symbol resolution not yet implemented");
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"Error binding breakpoint: {ex.Message}");
            _breakpointManager.SetVerified(bp.Id, false, ex.Message);
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
        _isAttached = false;
        IsRunning = false;
    }

    private string GetFunctionName(CorDebugFunction function)
    {
        try
        {
            var module = function.Module;
            var token = function.Token;
            return $"Function_{token:X}";
        }
        catch
        {
            return "Unknown";
        }
    }

    // Event handlers
    private void HandleProcessCreated(ICorDebugProcess process)
    {
        _logger?.Invoke("Process created event");
        _rawProcess = process;
        if (_stopAtEntry)
        {
            OnStopped?.Invoke(0, "entry");
        }
    }

    private void HandleProcessExited(ICorDebugProcess process, int exitCode)
    {
        _logger?.Invoke($"Process exited with code: {exitCode}");
        IsRunning = false;
        OnExited?.Invoke(exitCode);
        OnTerminated?.Invoke();
    }

    private void HandleThreadCreated(ICorDebugAppDomain appDomain, ICorDebugThread thread)
    {
        var corThread = new CorDebugThread(thread);
        _threads[corThread.Id] = corThread;
        OnThreadStarted?.Invoke(corThread.Id, $"Thread {corThread.Id}");
    }

    private void HandleThreadExited(ICorDebugAppDomain appDomain, ICorDebugThread thread)
    {
        var corThread = new CorDebugThread(thread);
        _threads.Remove(corThread.Id);
        OnThreadExited?.Invoke(corThread.Id, $"Thread {corThread.Id}");
    }

    private void HandleModuleLoaded(ICorDebugAppDomain appDomain, ICorDebugModule module)
    {
        var corModule = new CorDebugModule(module);
        var name = corModule.Name;
        OnModuleLoaded?.Invoke(name, name, name);
    }

    private void HandleBreakpoint(ICorDebugAppDomain appDomain, ICorDebugThread thread, ICorDebugBreakpoint breakpoint)
    {
        var corThread = new CorDebugThread(thread);
        IsRunning = false;
        OnStopped?.Invoke(corThread.Id, "breakpoint");
    }

    private void HandleStepComplete(ICorDebugAppDomain appDomain, ICorDebugThread thread)
    {
        var corThread = new CorDebugThread(thread);
        IsRunning = false;
        OnStopped?.Invoke(corThread.Id, "step");
    }

    private void HandleBreak(ICorDebugAppDomain appDomain, ICorDebugThread thread)
    {
        var corThread = new CorDebugThread(thread);
        IsRunning = false;
        OnStopped?.Invoke(corThread.Id, "pause");
    }

    private void HandleException(ICorDebugAppDomain appDomain, ICorDebugThread thread, ICorDebugFrame frame, int offset, CorDebugExceptionCallbackType eventType, CorDebugExceptionFlags flags)
    {
        var corThread = new CorDebugThread(thread);
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
