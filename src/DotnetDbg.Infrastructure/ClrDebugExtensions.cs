using ClrDebug;

namespace DotnetDbg.Infrastructure;

public static class ClrDebugExtensions
{
	public static CorDebug Automatic(DbgShim dbgshim, int pid)
    {
        IntPtr unregisterToken = IntPtr.Zero;

        CorDebug? cordebug = null;
        HRESULT hr = HRESULT.E_FAIL;
        var wait = new AutoResetEvent(false);

        try
        {
            /* If the process starts before GetStartupNotificationEvent inside RegisterForRuntimeStartup is called (e.g. because you were playing
             * in the debugger between launching the process and reaching this line of code) then WaitForSingleObject inside RegisterForRuntimeStartup
             * will hang indefinitely. You can prevent this by starting the process suspended.  In the Manual example, we call GetStartupNotificationEvent
             * ourselves, however in the Automatic example, RegisterForRuntimeStartup calls GetStartupNotificationEvent itself internally. In the latter scenario,
             * technically speaking there is the possibility of a race occurring even without us stepping in the debugger, but that's the risk you take when
             * you use RegisterForRuntimeStartup */

            //dbgshim.ResumeProcess(resumeHandle);     //Do not step! the CLR may initialize while you're stepping! Either set a breakpoint in the PSTARTUP_CALLBACK or AFTER RegisterForRuntimeStartup

            //Do not step! the CLR may initialize while you're stepping! Either set a breakpoint in the PSTARTUP_CALLBACK or AFTER RegisterForRuntimeStartup

            //Our DbgShim object will cache the last delegate passed to native code to prevent it being garbage collected.
            //As such there is no need to GC.KeepAlive() anything
            unregisterToken = dbgshim.RegisterForRuntimeStartup(pid, (pCordb, parameter, callbackHR) =>
            {
                /* DbgShim provides two overloads of RegisterForRuntimeStartup: one that takes a PSTARTUP_CALLBACK and one
                 * that takes a RuntimeStartupCallback. As it is not possible to easily marshal the ICorDebug parameter on the PSTARTUP_CALLBACK
                 * in all scenarios (.NET Core is buggy and NativeAOT is impossible on non-Windows platforms) we work around this by defining an
                 * RegisterForRuntimeStartup extension method that takes a "RuntimeStartupCallback" instead. This extension method defers to the "real"
                 * RegisterForRuntimeStartup internally and handles the marshalling/wrapping of the ICorDebug interface for us. If the HRESULT parameter
                 * passed to the callback is not S_OK, "pCordb" will be null. If the delegate type or delegate parameter types on the callback passed to
                 * RegisterForRuntimeStartup have not been explicitly specified, the compiler can still figure out which RegisterForRuntimeStartup
                 * overload to use based on the type of value "pCordb" is assigned to. */
                cordebug = pCordb;

                hr = callbackHR;

                wait.Set();
            });

            wait.WaitOne();
        }
        finally
        {
            if (unregisterToken != IntPtr.Zero)
                dbgshim.UnregisterForRuntimeStartup(unregisterToken);
        }

        //if callbackHR was not S_OK, an error occurred while attempting to register for runtime startup
        if (cordebug == null) throw new DebugException(hr);

        return cordebug;

        //Initialize ICorDebug, setup our managed callback and attach to the existing process
        //InitCorDebug(cordebug, pid);

        //while (true) Thread.Sleep(1);
    }

    private static void InitCorDebug(CorDebug cordebug, int pid)
    {
        cordebug.Initialize();

        var cb = new CorDebugManagedCallback();
        cb.OnAnyEvent += (s, e) => e.Controller.Continue(false);
        cb.OnLoadModule += LoadModule;

        cordebug.SetManagedHandler(cb);

        cordebug.DebugActiveProcess(pid, false);
    }

    private static void LoadModule(object? sender, LoadModuleCorDebugManagedCallbackEventArgs e)
    {
        Console.WriteLine($"Loaded {e.Module.Name}");
    }
}
