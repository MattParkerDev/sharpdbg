using System.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace DotnetDbg.Cli.Tests;

public static class TestHelper
{
	public static (DebugProtocolHost, TaskCompletionSource InitializedEventTcs, TaskCompletionSource<StoppedEvent>, Process DebugAdapterProcess, Process DebuggableProcess) GetRunningDebugProtocolHost(ITestOutputHelper testOutputHelper)
	{
	    var startSuspended = false;
		var process = DebugAdapterProcessHelper.GetDebugAdapterProcess();
		var debuggableProcess = DebuggableProcessHelper.StartDebuggableProcess(startSuspended);
		var initializedEventTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var debugProtocolHost = DebugAdapterProcessHelper.GetDebugProtocolHost(process, testOutputHelper, initializedEventTcs);
		var stoppedEventTcs = new TaskCompletionSource<StoppedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
		debugProtocolHost.RegisterEventType<StoppedEvent>(@event => stoppedEventTcs.TrySetResult(@event));
		debugProtocolHost.Run();
		return (debugProtocolHost, initializedEventTcs, stoppedEventTcs, process, debuggableProcess);
	}

	public static DebugProtocolHost WithInitializeRequest(this DebugProtocolHost debugProtocolHost)
	{
		var initializeRequest = DebugAdapterProcessHelper.GetInitializeRequest();
		debugProtocolHost.SendRequestSync(initializeRequest);
		return debugProtocolHost;
	}

	public static DebugProtocolHost WithAttachRequest(this DebugProtocolHost debugProtocolHost, int debuggableProcessId)
	{
		var attachRequest = DebugAdapterProcessHelper.GetAttachRequest(debuggableProcessId);
		debugProtocolHost.SendRequestSync(attachRequest);
		return debugProtocolHost;
	}
	public static async Task<DebugProtocolHost> WaitForInitializedEvent(this DebugProtocolHost debugProtocolHost, TaskCompletionSource initializedEventTcs)
	{
		await initializedEventTcs.Task.WaitAsync(TestContext.Current.CancellationToken);
		return debugProtocolHost;
	}

	public static DebugProtocolHost WithBreakpointsRequest(this DebugProtocolHost debugProtocolHost)
	{
		var setBreakpointsRequest = DebugAdapterProcessHelper.GetSetBreakpointsRequest();
		debugProtocolHost.SendRequestSync(setBreakpointsRequest);
		return debugProtocolHost;
	}

	public static DebugProtocolHost WithConfigurationDoneRequest(this DebugProtocolHost debugProtocolHost)
	{
		var configurationDoneRequest = new ConfigurationDoneRequest();
		debugProtocolHost.SendRequestSync(configurationDoneRequest);
		return debugProtocolHost;
	}

	public static DebugProtocolHost WithOptionalResumeRuntime(this DebugProtocolHost debugProtocolHost, int processId, bool startSuspended)
	{
	    // DiagnosticsClient.ResumeRuntime seems to have a different implementation on MacOS - it will throw if the runtime is not paused...
	    if (startSuspended) new DiagnosticsClient(processId).ResumeRuntime();
		return debugProtocolHost;
	}
}
