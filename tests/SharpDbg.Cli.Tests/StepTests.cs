using AwesomeAssertions;
using SharpDbg.Cli.Tests.Helpers;

namespace SharpDbg.Cli.Tests;

public class StepTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
    public async Task SharpDbgCli_StepRequests_Returns_StoppedEventsAtCorrectLocation()
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
	    var stopInfo2 = stoppedEvent2.ReadStopInfo();
	    stopInfo2.filePath.Should().EndWith("AnotherClass.cs");
	    stopInfo2.line.Should().Be(7);

	    var stoppedEvent3 = await debugProtocolHost
		    .WithStepOutRequest(stoppedEvent.ThreadId!.Value)
		    .WaitForStoppedEvent(stoppedEventTcs);
	    var stopInfo3 = stoppedEvent3.ReadStopInfo();
	    // Stepping out should land us back on the same line as the method we just stepped out of
	    stopInfo3.filePath.Should().EndWith("MyClass.cs");
	    stopInfo3.line.Should().Be(20);

	    // Continue so we loop and are back at the breakpoint
	    var stoppedEvent4 = await debugProtocolHost.WithContinueRequest().WaitForStoppedEvent(stoppedEventTcs);
	    var stopInfo4 = stoppedEvent4.ReadStopInfo();
	    stopInfo4.filePath.Should().EndWith("MyClass.cs");
	    stopInfo4.line.Should().Be(20);

	    // Now, put a breakpoint inside AnotherMethod, and step over. We should still be in AnotherClass.cs
	    debugProtocolHost.WithBreakpointsRequest(8, Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "Namespace1", "AnotherClass.cs"));

	    var stoppedEvent5 = await debugProtocolHost.WithStepOverRequest(stoppedEvent.ThreadId!.Value).WaitForStoppedEvent(stoppedEventTcs);
	    var stopInfo5 = stoppedEvent5.ReadStopInfo();
	    stopInfo5.filePath.Should().EndWith("AnotherClass.cs");
	    stopInfo5.line.Should().Be(8);

	    var stoppedEvent6 = await debugProtocolHost.WithStepOverRequest(stoppedEvent.ThreadId!.Value).WaitForStoppedEvent(stoppedEventTcs);
	    var stopInfo6 = stoppedEvent6.ReadStopInfo();
	    stopInfo6.filePath.Should().EndWith("AnotherClass.cs");
	    stopInfo6.line.Should().Be(9);

	    var stoppedEvent7 = await debugProtocolHost.WithContinueRequest().WaitForStoppedEvent(stoppedEventTcs);
	    var stopInfo7 = stoppedEvent7.ReadStopInfo();
	    stopInfo7.filePath.Should().EndWith("MyClass.cs");
	    stopInfo7.line.Should().Be(20);

	    // breakpoint on a line that would F11 into unmapped code
	    debugProtocolHost.WithBreakpointsRequest(10, Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "Namespace1", "AnotherClass.cs"));
	    var stoppedEvent8 = await debugProtocolHost.WithContinueRequest().WaitForStoppedEvent(stoppedEventTcs);
	    var stopInfo8 = stoppedEvent8.ReadStopInfo();
	    stopInfo8.filePath.Should().EndWith("AnotherClass.cs");
	    stopInfo8.line.Should().Be(10);

	    // ensure that we do not receive stop info with no source
	    var stoppedEvent9 = await debugProtocolHost.WithStepInRequest(stoppedEvent.ThreadId!.Value).WaitForStoppedEvent(stoppedEventTcs);
	    var stopInfo9 = stoppedEvent9.ReadStopInfo();
	    stopInfo9.filePath.Should().EndWith("AnotherClass.cs");
	    stopInfo9.line.Should().Be(10);

	    List<int> threadIds = [stoppedEvent.ThreadId!.Value, stoppedEvent2.ThreadId!.Value, stoppedEvent3.ThreadId!.Value, stoppedEvent4.ThreadId!.Value, stoppedEvent5.ThreadId!.Value, stoppedEvent6.ThreadId!.Value, stoppedEvent7.ThreadId!.Value];
	    threadIds.Distinct().Should().HaveCount(1);
    }
}
