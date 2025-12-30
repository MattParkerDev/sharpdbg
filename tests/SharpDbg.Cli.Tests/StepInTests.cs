using AwesomeAssertions;
using SharpDbg.Cli.Tests.Helpers;

namespace SharpDbg.Cli.Tests;

public class StepInTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
    public async Task SharpDbgCli_StepInRequest_Returns_StoppedEventAtCorrectLocation()
    {
	    var startSuspended = true;

	    var (debugProtocolHost, initializedEventTcs, stoppedEventTcs, adapter, p2) = TestHelper.GetRunningDebugProtocolHostInProc(testOutputHelper, startSuspended);
	    using var _ = adapter;
	    using var __ = new ProcessKiller(p2);

	    await debugProtocolHost
		    .WithInitializeRequest()
		    .WithAttachRequest(p2.Id)
		    .WaitForInitializedEvent(initializedEventTcs);
	    debugProtocolHost
		    .WithBreakpointsRequest(20)
		    .WithConfigurationDoneRequest()
		    .WithOptionalResumeRuntime(p2.Id, startSuspended);

	    var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(stoppedEventTcs);
	    var stopInfo = stoppedEvent.ReadStopInfo();
	    stopInfo.filePath.Should().EndWith("MyClass.cs");
	    stopInfo.line.Should().Be(20);

	    var stoppedEvent2 = await debugProtocolHost
		    .WithStepInRequest(stoppedEvent.ThreadId!.Value)
		    .WaitForStoppedEvent(stoppedEventTcs);
	    //var stopInfo2 = stoppedEvent2.ReadStopInfo();
	    //stopInfo2.filePath.Should().EndWith("AnotherClass.cs");
	    //stopInfo2.line.Should().Be(8);
    }
}
