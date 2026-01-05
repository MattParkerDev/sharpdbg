using AwesomeAssertions;
using SharpDbg.Cli.Tests.Helpers;

namespace SharpDbg.Cli.Tests;

public class AsyncStepTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
    public async Task SharpDbgCli_StepRequests_InAsyncMethod_Returns_StoppedEventsAtCorrectLocation()
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
		    .WithBreakpointsRequest(9, Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "MyAsyncClass.cs"))
		    .WithConfigurationDoneRequest()
		    .WithOptionalResumeRuntime(p2.Id, startSuspended);

	    var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(stoppedEventTcs);
	    var stopInfo = stoppedEvent.ReadStopInfo();
	    stopInfo.filePath.Should().EndWith("MyAsyncClass.cs");
	    stopInfo.line.Should().Be(9);

	    debugProtocolHost.WithClearBreakpointsRequest(Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "MyAsyncClass.cs"));

	    // step over sync
	    var stoppedEvent2 = await debugProtocolHost.WithStepOverRequest(stoppedEvent.ThreadId!.Value).WaitForStoppedEvent(stoppedEventTcs);
	    var stopInfo2 = stoppedEvent2.ReadStopInfo();
	    stopInfo2.filePath.Should().EndWith("MyAsyncClass.cs");
	    stopInfo2.line.Should().Be(10);

	    // step over sync, arrives at await line
	    var stoppedEvent3 = await debugProtocolHost.WithStepOverRequest(stoppedEvent.ThreadId!.Value).WaitForStoppedEvent(stoppedEventTcs);
	    var stopInfo3 = stoppedEvent3.ReadStopInfo();
	    stopInfo3.filePath.Should().EndWith("MyAsyncClass.cs");
	    stopInfo3.line.Should().Be(11);

	    // step over await
	    var stoppedEvent4 = await debugProtocolHost.WithStepOverRequest(stoppedEvent.ThreadId!.Value).WaitForStoppedEvent(stoppedEventTcs);
	    var stopInfo4 = stoppedEvent4.ReadStopInfo();
	    stopInfo4.filePath.Should().EndWith("MyAsyncClass.cs");
	    stopInfo4.line.Should().Be(12);

	    // step over another await, note we must use stoppedEvent4's ThreadId, as the thread may have changed after the await
	    var stoppedEvent5 = await debugProtocolHost.WithStepOverRequest(stoppedEvent4.ThreadId!.Value).WaitForStoppedEvent(stoppedEventTcs);
	    var stopInfo5 = stoppedEvent5.ReadStopInfo();
	    stopInfo5.filePath.Should().EndWith("MyAsyncClass.cs");
	    stopInfo5.line.Should().Be(13);

	    // step into an await method
	    var stoppedEvent6 = await debugProtocolHost.WithStepInRequest(stoppedEvent5.ThreadId!.Value).WaitForStoppedEvent(stoppedEventTcs);
	    var stopInfo6 = stoppedEvent6.ReadStopInfo();
	    stopInfo6.filePath.Should().EndWith("AnotherClass.cs");
	    stopInfo6.line.Should().Be(17);

	    // step over
	    var stoppedEvent7 = await debugProtocolHost.WithStepInRequest(stoppedEvent5.ThreadId!.Value).WaitForStoppedEvent(stoppedEventTcs);
	    var stopInfo7 = stoppedEvent7.ReadStopInfo();
	    stopInfo7.filePath.Should().EndWith("AnotherClass.cs");
	    stopInfo7.line.Should().Be(18);

	    // step out of an async await method
	    // if JMC is enabled, this lands us on the line after the invocation of the async method (ie line 14)
	    // if JMC is disabled, we land on the invocation line (ie line 13)
	    var stoppedEvent8 = await debugProtocolHost.WithStepOutRequest(stoppedEvent5.ThreadId!.Value).WaitForStoppedEvent(stoppedEventTcs);
	    var stopInfo8 = stoppedEvent8.ReadStopInfo();
	    stopInfo8.filePath.Should().EndWith("MyAsyncClass.cs");
	    stopInfo8.line.Should().Be(13);

	    // step over
	    var stoppedEvent9 = await debugProtocolHost.WithStepOverRequest(stoppedEvent5.ThreadId!.Value).WaitForStoppedEvent(stoppedEventTcs);
	    var stopInfo9 = stoppedEvent9.ReadStopInfo();
	    stopInfo9.filePath.Should().EndWith("MyAsyncClass.cs");
	    stopInfo9.line.Should().Be(14);

	    // step into async void method
	    var stoppedEvent10 = await debugProtocolHost.WithStepInRequest(stoppedEvent5.ThreadId!.Value).WaitForStoppedEvent(stoppedEventTcs);
	    var stopInfo10 = stoppedEvent10.ReadStopInfo();
	    stopInfo10.filePath.Should().EndWith("AnotherClass.cs");
	    stopInfo10.line.Should().Be(24);

	    // step out of async void method
	    var stoppedEvent11 = await debugProtocolHost.WithStepOutRequest(stoppedEvent5.ThreadId!.Value).WaitForStoppedEvent(stoppedEventTcs);
	    var stopInfo11 = stoppedEvent11.ReadStopInfo();
	    stopInfo11.filePath.Should().EndWith("MyAsyncClass.cs");
	    stopInfo11.line.Should().Be(14);
    }
}
