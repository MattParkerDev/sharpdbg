using ClrDebug;

namespace DotnetDbg.Infrastructure.Debugger;

/// <summary>
/// Implementation of ICorDebugManagedCallback for receiving debug events
/// </summary>
public class DebuggerCallbacks : ICorDebugManagedCallback, ICorDebugManagedCallback2
{
    private readonly Action<string>? _logger;

    public event Action<ICorDebugProcess>? OnProcessCreated;
    public event Action<ICorDebugProcess, int>? OnProcessExited;
    public event Action<ICorDebugAppDomain, ICorDebugThread>? OnThreadCreated;
    public event Action<ICorDebugAppDomain, ICorDebugThread>? OnThreadExited;
    public event Action<ICorDebugAppDomain, ICorDebugModule>? OnModuleLoaded;
    public event Action<ICorDebugAppDomain, ICorDebugModule>? OnModuleUnloaded;
    public event Action<ICorDebugAppDomain, ICorDebugThread, ICorDebugBreakpoint>? OnBreakpoint;
    public event Action<ICorDebugAppDomain, ICorDebugThread>? OnStepComplete;
    public event Action<ICorDebugAppDomain, ICorDebugThread>? OnBreak;
    public event Action<ICorDebugAppDomain, ICorDebugThread, ICorDebugFrame, int, CorDebugExceptionCallbackType, CorDebugExceptionFlags>? OnException;

    public DebuggerCallbacks(Action<string>? logger = null)
    {
        _logger = logger;
    }

    public HRESULT CreateProcess(ICorDebugProcess pProcess)
    {
        var process = new CorDebugProcess(pProcess);
        _logger?.Invoke($"CreateProcess: {process.Id}");
        OnProcessCreated?.Invoke(pProcess);
        pProcess.Continue(false);
        return HRESULT.S_OK;
    }

    public HRESULT ExitProcess(ICorDebugProcess pProcess)
    {
        var process = new CorDebugProcess(pProcess);
        _logger?.Invoke($"ExitProcess: {process.Id}");
        OnProcessExited?.Invoke(pProcess, 0);
        return HRESULT.S_OK;
    }

    public HRESULT CreateThread(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread)
    {
        var thread = new CorDebugThread(pThread);
        _logger?.Invoke($"CreateThread: {thread.Id}");
        OnThreadCreated?.Invoke(pAppDomain, pThread);
        pAppDomain.Continue(false);
        return HRESULT.S_OK;
    }

    public HRESULT ExitThread(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread)
    {
        var thread = new CorDebugThread(pThread);
        _logger?.Invoke($"ExitThread: {thread.Id}");
        OnThreadExited?.Invoke(pAppDomain, pThread);
        pAppDomain.Continue(false);
        return HRESULT.S_OK;
    }

    public HRESULT LoadModule(ICorDebugAppDomain pAppDomain, ICorDebugModule pModule)
    {
        var module = new CorDebugModule(pModule);
        _logger?.Invoke($"LoadModule: {module.Name}");
        OnModuleLoaded?.Invoke(pAppDomain, pModule);
        pAppDomain.Continue(false);
        return HRESULT.S_OK;
    }

    public HRESULT UnloadModule(ICorDebugAppDomain pAppDomain, ICorDebugModule pModule)
    {
        var module = new CorDebugModule(pModule);
        _logger?.Invoke($"UnloadModule: {module.Name}");
        OnModuleUnloaded?.Invoke(pAppDomain, pModule);
        pAppDomain.Continue(false);
        return HRESULT.S_OK;
    }

    public HRESULT LoadClass(ICorDebugAppDomain pAppDomain, ICorDebugClass c)
    {
        _logger?.Invoke("LoadClass");
        pAppDomain.Continue(false);
        return HRESULT.S_OK;
    }

    public HRESULT UnloadClass(ICorDebugAppDomain pAppDomain, ICorDebugClass c)
    {
        _logger?.Invoke("UnloadClass");
        pAppDomain.Continue(false);
        return HRESULT.S_OK;
    }

    public HRESULT Breakpoint(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, ICorDebugBreakpoint pBreakpoint)
    {
        var thread = new CorDebugThread(pThread);
        _logger?.Invoke($"Breakpoint hit on thread {thread.Id}");
        OnBreakpoint?.Invoke(pAppDomain, pThread, pBreakpoint);
        return HRESULT.S_OK;
    }

    public HRESULT StepComplete(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, ICorDebugStepper pStepper, CorDebugStepReason reason)
    {
        var thread = new CorDebugThread(pThread);
        _logger?.Invoke($"StepComplete on thread {thread.Id}, reason: {reason}");
        OnStepComplete?.Invoke(pAppDomain, pThread);
        return HRESULT.S_OK;
    }

    public HRESULT Break(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread)
    {
        var thread = new CorDebugThread(pThread);
        _logger?.Invoke($"Break on thread {thread.Id}");
        OnBreak?.Invoke(pAppDomain, pThread);
        return HRESULT.S_OK;
    }

    public HRESULT Exception(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, int unhandled)
    {
        var thread = new CorDebugThread(pThread);
        _logger?.Invoke($"Exception on thread {thread.Id}, unhandled: {unhandled}");
        pAppDomain.Continue(false);
        return HRESULT.S_OK;
    }

    public HRESULT EvalComplete(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, ICorDebugEval pEval)
    {
        var thread = new CorDebugThread(pThread);
        _logger?.Invoke($"EvalComplete on thread {thread.Id}");
        pAppDomain.Continue(false);
        return HRESULT.S_OK;
    }

    public HRESULT EvalException(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, ICorDebugEval pEval)
    {
        var thread = new CorDebugThread(pThread);
        _logger?.Invoke($"EvalException on thread {thread.Id}");
        pAppDomain.Continue(false);
        return HRESULT.S_OK;
    }

    public HRESULT CreateAppDomain(ICorDebugProcess pProcess, ICorDebugAppDomain pAppDomain)
    {
        var appDomain = new CorDebugAppDomain(pAppDomain);
        _logger?.Invoke($"CreateAppDomain: {appDomain.Name}");
        pProcess.Continue(false);
        return HRESULT.S_OK;
    }

    public HRESULT ExitAppDomain(ICorDebugProcess pProcess, ICorDebugAppDomain pAppDomain)
    {
        var appDomain = new CorDebugAppDomain(pAppDomain);
        _logger?.Invoke($"ExitAppDomain: {appDomain.Name}");
        pProcess.Continue(false);
        return HRESULT.S_OK;
    }

    public HRESULT LoadAssembly(ICorDebugAppDomain pAppDomain, ICorDebugAssembly pAssembly)
    {
        var assembly = new CorDebugAssembly(pAssembly);
        _logger?.Invoke($"LoadAssembly: {assembly.Name}");
        pAppDomain.Continue(false);
        return HRESULT.S_OK;
    }

    public HRESULT UnloadAssembly(ICorDebugAppDomain pAppDomain, ICorDebugAssembly pAssembly)
    {
        var assembly = new CorDebugAssembly(pAssembly);
        _logger?.Invoke($"UnloadAssembly: {assembly.Name}");
        pAppDomain.Continue(false);
        return HRESULT.S_OK;
    }

    public HRESULT ControlCTrap(ICorDebugProcess pProcess)
    {
        _logger?.Invoke("ControlCTrap");
        pProcess.Continue(false);
        return HRESULT.S_OK;
    }

    public HRESULT NameChange(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread)
    {
        _logger?.Invoke("NameChange");
        pAppDomain?.Continue(false);
        return HRESULT.S_OK;
    }

    public HRESULT UpdateModuleSymbols(ICorDebugAppDomain pAppDomain, ICorDebugModule pModule, IStream pSymbolStream)
    {
        var module = new CorDebugModule(pModule);
        _logger?.Invoke($"UpdateModuleSymbols: {module.Name}");
        pAppDomain.Continue(false);
        return HRESULT.S_OK;
    }

    public HRESULT EditAndContinueRemap(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, ICorDebugFunction pFunction, bool fAccurate)
    {
        _logger?.Invoke("EditAndContinueRemap");
        pAppDomain.Continue(false);
        return HRESULT.S_OK;
    }

    public HRESULT BreakpointSetError(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, ICorDebugBreakpoint pBreakpoint, int dwError)
    {
        _logger?.Invoke($"BreakpointSetError: {dwError}");
        pAppDomain.Continue(false);
        return HRESULT.S_OK;
    }

    public HRESULT LogMessage(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, LoggingLevelEnum lLevel, string pLogSwitchName, string pMessage)
    {
        _logger?.Invoke($"LogMessage: {pMessage}");
        pAppDomain.Continue(false);
        return HRESULT.S_OK;
    }

    public HRESULT LogSwitch(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, int lLevel, LogSwitchCallReason lReason, string pLogSwitchName, string pParentName)
    {
        _logger?.Invoke($"LogSwitch: {pLogSwitchName}");
        pAppDomain.Continue(false);
        return HRESULT.S_OK;
    }

    public HRESULT DebuggerError(ICorDebugProcess pProcess, HRESULT errorHR, int errorCode)
    {
        _logger?.Invoke($"DebuggerError: HR={errorHR}, Code={errorCode}");
        return HRESULT.S_OK;
    }

    // ICorDebugManagedCallback2 methods
    public HRESULT FunctionRemapOpportunity(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, ICorDebugFunction pOldFunction, ICorDebugFunction pNewFunction, int oldILOffset)
    {
        _logger?.Invoke("FunctionRemapOpportunity");
        pAppDomain.Continue(false);
        return HRESULT.S_OK;
    }

    public HRESULT CreateConnection(ICorDebugProcess pProcess, int dwConnectionId, string pConnName)
    {
        _logger?.Invoke($"CreateConnection: {dwConnectionId}");
        pProcess.Continue(false);
        return HRESULT.S_OK;
    }

    public HRESULT ChangeConnection(ICorDebugProcess pProcess, int dwConnectionId)
    {
        _logger?.Invoke($"ChangeConnection: {dwConnectionId}");
        pProcess.Continue(false);
        return HRESULT.S_OK;
    }

    public HRESULT DestroyConnection(ICorDebugProcess pProcess, int dwConnectionId)
    {
        _logger?.Invoke($"DestroyConnection: {dwConnectionId}");
        pProcess.Continue(false);
        return HRESULT.S_OK;
    }

    public HRESULT Exception(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, ICorDebugFrame pFrame, int nOffset, CorDebugExceptionCallbackType dwEventType, CorDebugExceptionFlags dwFlags)
    {
        var thread = new CorDebugThread(pThread);
        _logger?.Invoke($"Exception2: thread={thread.Id}, type={dwEventType}");
        OnException?.Invoke(pAppDomain, pThread, pFrame, nOffset, dwEventType, dwFlags);
        return HRESULT.S_OK;
    }

    public HRESULT ExceptionUnwind(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, CorDebugExceptionUnwindCallbackType dwEventType, CorDebugExceptionFlags dwFlags)
    {
        _logger?.Invoke($"ExceptionUnwind: type={dwEventType}");
        pAppDomain.Continue(false);
        return HRESULT.S_OK;
    }

    public HRESULT FunctionRemapComplete(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, ICorDebugFunction pFunction)
    {
        _logger?.Invoke("FunctionRemapComplete");
        pAppDomain.Continue(false);
        return HRESULT.S_OK;
    }

    public HRESULT MDANotification(ICorDebugController pController, ICorDebugThread pThread, ICorDebugMDA pMDA)
    {
        _logger?.Invoke("MDANotification");
        pController.Continue(false);
        return HRESULT.S_OK;
    }
}
