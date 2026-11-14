using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Ardalis.GuardClauses;
using ICorDebugSharp;
using Microsoft.Diagnostics.NETCore.Client;
using SharpDbg.Infrastructure.Debugger.Models;

namespace SharpDbg.Infrastructure;

// Originally based on https://github.com/lordmilko/ClrDebug/blob/5f46218f4b840ab8a94920623dc263b5f2334138/Samples/NetCore/Program.cs
public static class ClrDebugExtensions
{
	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
	public static unsafe void OnRuntimeStartup(void* pCorDebug, void* parameter, int hr)
	{
		var corDebug = ComInterfaceMarshaller<ICorDebug>.ConvertToManaged(pCorDebug);
		var runtimeStartupTcs = GCHandle.FromIntPtr((IntPtr)parameter).Target as TaskCompletionSource<(ICorDebug? CorDebug, int Hr)>;
		Guard.Against.Null(runtimeStartupTcs);
		runtimeStartupTcs.SetResult((corDebug, hr));
	}

	public static ICorDebug Mobile(RemoteAttachInfo remoteAttachInfo)
	{
		var result = DbgShim.RegisterForRuntimeStartupRemotePort(
			remoteAttachInfo.Address,
			checked((uint)remoteAttachInfo.Port),
			remoteAttachInfo.Platform,
			remoteAttachInfo.IsServer,
			remoteAttachInfo.MscordbiPath,
			remoteAttachInfo.AssembliesPath, out var corDebug);
		Marshal.ThrowExceptionForHR(result);

		Guard.Against.Null(corDebug);
		return corDebug;
	}

	/// pass resumeDiagnosticSuspension true if the process was launched with the DOTNET_DefaultDiagnosticPortSuspend environment variable, and you wish for it to be resumed after RegisterForRuntimeStartup
	public static async Task<ICorDebug> Automatic(int pid, bool resumeDiagnosticSuspension = false)
	{
		using var registration = RegisterForRuntimeStartup(pid);
		if (resumeDiagnosticSuspension) await DiagnosticClientHelper.DiagnosticClientResumeRuntime(pid);
		return await registration.WaitForCorDebug().ConfigureAwait(false);
	}

	/// <summary>
	/// Registers for the runtime in <paramref name="pid"/> starting, returning once dbgshim is listening for it
	/// </summary>
	/// <remarks>
	/// A process started with DOTNET_DefaultDiagnosticPortSuspend must only be resumed after this returns. If the runtime gets past its
	/// startup notification while dbgshim is still registering, dbgshim can wait for a startup event that is never signalled
	/// </remarks>
	public static RuntimeStartupRegistration RegisterForRuntimeStartup(int pid)
	{
		var runtimeStartupTcs = new TaskCompletionSource<(ICorDebug? CorDebug, int Hr)>(TaskCreationOptions.RunContinuationsAsynchronously);
		var runtimeStartupTcsHandle = GCHandle.Alloc(runtimeStartupTcs);
		try
		{
			IntPtr unregisterToken;
			unsafe
			{
				var registerHr = DbgShim.RegisterForRuntimeStartup(checked((uint)pid), &OnRuntimeStartup, GCHandle.ToIntPtr(runtimeStartupTcsHandle), out unregisterToken);
				Marshal.ThrowExceptionForHR(registerHr);
			}
			return new RuntimeStartupRegistration(runtimeStartupTcs.Task, unregisterToken, runtimeStartupTcsHandle);
		}
		catch
		{
			runtimeStartupTcsHandle.Free();
			throw;
		}
	}

	public sealed class RuntimeStartupRegistration(Task<(ICorDebug? CorDebug, int Hr)> runtimeStartup, IntPtr unregisterToken, GCHandle runtimeStartupTcsHandle) : IDisposable
	{
		private GCHandle _runtimeStartupTcsHandle = runtimeStartupTcsHandle;

		public async Task<ICorDebug> WaitForCorDebug()
		{
			var (cordebug, hr) = await runtimeStartup.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
			//if callbackHR was not S_OK, an error occurred while attempting to register for runtime startup
			if (cordebug is null) throw new InvalidOperationException($"Attempting to register for runtime startup failed: {hr}");
			return cordebug;
		}

		public void Dispose()
		{
			if (_runtimeStartupTcsHandle.IsAllocated is false) return;
			if (unregisterToken != IntPtr.Zero) DbgShim.UnregisterForRuntimeStartup(unregisterToken);
			_runtimeStartupTcsHandle.Free();
		}
	}

	// public static ICorDebug Manual(int pid)
	// {
	// 	/* If the process initializes the CLR before GetStartupNotificationEvent is called (e.g. because you were playing in the debugger between launching
	// 	 * the process and reaching this line of code) then WaitForSingleObject below will hang indefinitely. You can prevent this by starting the process suspended.
	// 	 * This event is signalled by debugger.cpp!OpenStartupNotificationEvent() which is called by NotifyDebuggerOfStartup(). Immediately after the startup event
	// 	 * is signalled, the CLR waits on g_hContinueStartupEvent which is one of the three components that comprise the global CLR_ENGINE_METRICS g_CLREngineMetrics! */
	// 	var startupEvent = DbgShim.GetStartupNotificationEvent(pid);
	//
	// 	//The event WaitForSingleObject is waiting on won't occur unless the process is resumed
	// 	//DbgShim.ResumeProcess(resumeHandle);
	//
	// 	// //As stated above, if you started the process suspended, you need to resume the process otherwise the CLR will never be loaded.
	// 	// var waitResult = NativeMethods.WaitForSingleObject(startupEvent, -1);
	// 	//
	// 	// if (waitResult != 0)
	// 	//     throw new InvalidOperationException($"Failed to get startup event. Is the target process a .NET Core application? Wait Result: {waitResult}");
	//
	// 	var enumResult = DbgShim.EnumerateCLRs(pid);
	//
	// 	try
	// 	{
	// 		var runtime = enumResult.Items.Single();
	//
	// 		//Version String is a comma delimited value containing dbiVersion, pidDebuggee, hmodTargetCLR
	// 		var versionStr = DbgShim.CreateVersionStringFromModule(pid, runtime.Path);
	//
	// 		/* Cordb::CheckCompatibility seems to be the only place where our debugger version is actually used,
	// 		 * and it says that if the version is 4, its major version 4. Version 4.5 is treated as an "unrecognized future version"
	// 		 * and is assigned major version 5, which is wrong. Cordb::CheckCompatibility then calls CordbProcess::IsCompatibleWith
	// 		 * which doesn't actually seem to do anything either, despite what all the docs in it would imply. */
	// 		var cordebug = DbgShim.CreateDebuggingInterfaceFromVersionEx(CorDebugInterfaceVersion.CorDebugVersion_4_0, versionStr);
	// 		return cordebug;
	// 		//Initialize ICorDebug, setup our managed callback and attach to the existing process. We attach while the CLR is blocked waiting for the "continue" event to be called
	// 		//InitCorDebug(cordebug, pid);
	//
	// 		/* There exists a structure CLR_ENGINE_METRICS within in coreclr.dll which is exported at ordinal 2. This structure indicates the RVA of the actual continue event that should be signalled
	// 		 * to indicate the CLR can continue starting. But how does the CLR know to wait on this event at all? In debugger.cpp!NotifyDebuggerOfStartup() it calls
	// 		 * OpenStartupNotificationEvent(). If that returns the event that was created by GetStartupNotificationEvent() then that event is set and closed,
	// 		 * and then g_hContinueStartupEvent is waited on infinitely. g_hContinueStartupEvent is one of the components that make up the CLR_ENGINE_METRICS g_CLREngineMetrics,
	// 		 * hence it all comes full circle. */
	// 		//NativeMethods.SetEvent(runtime.Handle);
	// 	}
	// 	finally
	// 	{
	// 		//CloseCLREnumeration does not call WakeRuntimes(), hence we MUST call SetEvent above.
	// 		//WakeRuntimes is called in InvokeStartupCallback() and UnregisterForRuntimeStartup() -> Unregister()
	// 		DbgShim.CloseCLREnumeration(enumResult);
	// 	}
	//
	// 	//while (true) Thread.Sleep(1);
	// }
}
