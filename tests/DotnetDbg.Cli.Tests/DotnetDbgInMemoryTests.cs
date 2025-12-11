using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace DotnetDbg.Cli.Tests;

public class DotnetDbgInMemoryTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
    public async Task DotnetDbgCli_StackTraceRequest_Returns()
    {
	    var startSuspended = false;
	    var process = DebugAdapterProcessHelper.GetDebugAdapterProcess();
	    var debuggableProcess = DebuggableProcessHelper.StartDebuggableProcess(startSuspended);
	    try
	    {
		    var initializedEventTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		    var (input, output) = InMemoryDebugAdapterHelper.GetAdapterStreams(testOutputHelper);

		    //var debugProtocolHost = DebugAdapterProcessHelper.GetDebugProtocolHost(output, input, testOutputHelper, initializedEventTcs);
		    var debugProtocolHost = DebugAdapterProcessHelper.GetDebugProtocolHost(process, testOutputHelper, initializedEventTcs);
		    var stoppedEventTcs = new TaskCompletionSource<StoppedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
		    debugProtocolHost.RegisterEventType<StoppedEvent>(@event => stoppedEventTcs.TrySetResult(@event));
			debugProtocolHost.Run();
		    var initializeRequest = DebugAdapterProcessHelper.GetInitializeRequest();
		    debugProtocolHost.SendRequestSync(initializeRequest);
		    var attachRequest = DebugAdapterProcessHelper.GetAttachRequest(debuggableProcess.Id);
		    debugProtocolHost.SendRequestSync(attachRequest);
		    await initializedEventTcs.Task;
		    var setBreakpointsRequest = DebugAdapterProcessHelper.GetSetBreakpointsRequest();
		    var breakpointsResponse = debugProtocolHost.SendRequestSync(setBreakpointsRequest);

		    var configurationDoneRequest = new ConfigurationDoneRequest();
		    debugProtocolHost.SendRequestSync(configurationDoneRequest);
		    // DiagnosticsClient.ResumeRuntime seems to have a different implementation on MacOS - it will throw if the runtime is not paused...
		    if (startSuspended) new DiagnosticsClient(debuggableProcess.Id).ResumeRuntime();

		    var stoppedEvent = await stoppedEventTcs.Task;
		    ;
		    var stackTraceRequest = new StackTraceRequest { ThreadId = stoppedEvent.ThreadId!.Value, StartFrame = 0, Levels = 1 };
		    var stackTraceResponse = debugProtocolHost.SendRequestSync(stackTraceRequest);
		    await Verify(stackTraceResponse);
	    }
	    finally
	    {
		    debuggableProcess.Kill();
	    }
    }
}
