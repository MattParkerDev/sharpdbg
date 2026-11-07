using AwesomeAssertions;
using SharpDbg.DapProtocol;
using SharpDbg.Cli.Tests.Helpers;

namespace SharpDbg.Cli.Tests;

public class AsyncStepTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task SharpDbgCli_StepRequests_InAsyncMethod_Returns_StoppedEventsAtCorrectLocation()
	{
		var startSuspended = true;
		var (debugProtocolHost, adapter, p2) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(p2.Id)
			.WaitForInitializedEvent();
		debugProtocolHost
			.WithBreakpointsRequest(9, Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "MyAsyncClass.cs"))
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
		var stopInfo = stoppedEvent.ReadStopInfo();
		stopInfo.filePath.Should().EndWith("MyAsyncClass.cs");
		stopInfo.line.Should().Be(9);

		debugProtocolHost.WithClearBreakpointsRequest(Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "MyAsyncClass.cs"));

		// step over sync
		var stoppedEvent2 = await debugProtocolHost.WithStepOverRequest(stoppedEvent.ThreadId!.Value).WaitForStoppedEvent();
		var stopInfo2 = stoppedEvent2.ReadStopInfo();
		stopInfo2.filePath.Should().EndWith("MyAsyncClass.cs");
		stopInfo2.line.Should().Be(10);

		// step over sync, arrives at await line
		var stoppedEvent3 = await debugProtocolHost.WithStepOverRequest(stoppedEvent2.ThreadId!.Value).WaitForStoppedEvent();
		var stopInfo3 = stoppedEvent3.ReadStopInfo();
		stopInfo3.filePath.Should().EndWith("MyAsyncClass.cs");
		stopInfo3.line.Should().Be(11);

		// step over await
		var stoppedEvent4 = await debugProtocolHost.WithStepOverRequest(stoppedEvent3.ThreadId!.Value).WaitForStoppedEvent();
		var stopInfo4 = stoppedEvent4.ReadStopInfo();
		stopInfo4.filePath.Should().EndWith("MyAsyncClass.cs");
		stopInfo4.line.Should().Be(12);

		// step over another await, note we must use stoppedEvent4's ThreadId, as the thread may have changed after the await
		var stoppedEvent5 = await debugProtocolHost.WithStepOverRequest(stoppedEvent4.ThreadId!.Value).WaitForStoppedEvent();
		var stopInfo5 = stoppedEvent5.ReadStopInfo();
		stopInfo5.filePath.Should().EndWith("MyAsyncClass.cs");
		stopInfo5.line.Should().Be(13);

		// step into an await method
		var stoppedEvent6 = await debugProtocolHost.WithStepInRequest(stoppedEvent5.ThreadId!.Value).WaitForStoppedEvent();
		var stopInfo6 = stoppedEvent6.ReadStopInfo();
		stopInfo6.filePath.Should().EndWith("AnotherClass.cs");
		stopInfo6.line.Should().Be(18);

		// step over
		var stoppedEvent7 = await debugProtocolHost.WithStepInRequest(stoppedEvent6.ThreadId!.Value).WaitForStoppedEvent();
		var stopInfo7 = stoppedEvent7.ReadStopInfo();
		stopInfo7.filePath.Should().EndWith("AnotherClass.cs");
		stopInfo7.line.Should().Be(19);

		// step out of an async await method
		var stoppedEvent8 = await debugProtocolHost.WithStepOutRequest(stoppedEvent7.ThreadId!.Value).WaitForStoppedEvent();
		var stopInfo8 = stoppedEvent8.ReadStopInfo();
		stopInfo8.filePath.Should().EndWith("MyAsyncClass.cs");
		stopInfo8.line.Should().Be(14);

		// step into async void method
		var stoppedEvent10 = await debugProtocolHost.WithStepInRequest(stoppedEvent8.ThreadId!.Value).WaitForStoppedEvent();
		var stopInfo10 = stoppedEvent10.ReadStopInfo();
		stopInfo10.filePath.Should().EndWith("AnotherClass.cs");
		stopInfo10.line.Should().Be(25);

		// step out of async void method
		var stoppedEvent11 = await debugProtocolHost.WithStepOutRequest(stoppedEvent10.ThreadId!.Value).WaitForStoppedEvent();
		var stopInfo11 = stoppedEvent11.ReadStopInfo();
		stopInfo11.filePath.Should().EndWith("MyAsyncClass.cs");
		stopInfo11.line.Should().Be(14);
	}

	// An async step-out waits for the method's task and stops in the awaiting caller, on the statement after the await. Without
	// Just My Code the step must still land there, not in the builder or the thread pool code that resumes the caller
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task SharpDbgCli_StepOutRequest_OfAsyncMethods_StopsInTheCaller(bool justMyCode)
	{
		var startSuspended = true;
		var (debugProtocolHost, adapter, p2) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;

		var filePath = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "AsyncStepOutClass.cs");
		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(p2.Id, justMyCode)
			.WaitForInitializedEvent();
		debugProtocolHost
			.WithBreakpointsRequest([18, 24, 30], filePath)
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		// ValueTask: AsyncValueTaskMethodBuilder's task is reached through ObjectIdForDebugger
		var valueTaskStop = await debugProtocolHost.WaitForStoppedEvent();
		valueTaskStop.ReadStopInfo().line.Should().Be(18);
		var afterValueTask = await debugProtocolHost.WithStepOutRequest(valueTaskStop.ThreadId!.Value).WaitForStoppedEvent();
		afterValueTask.Reason.Should().Be(StoppedEvent.ReasonValue.Step);
		afterValueTask.ReadStopInfo().line.Should().Be(9);

		var taskStop = await debugProtocolHost.WithContinueRequest().WaitForStoppedEvent();
		taskStop.ReadStopInfo().line.Should().Be(24);
		var afterTask = await debugProtocolHost.WithStepOutRequest(taskStop.ThreadId!.Value).WaitForStoppedEvent();
		afterTask.Reason.Should().Be(StoppedEvent.ReasonValue.Step);
		afterTask.ReadStopInfo().line.Should().Be(10);

		// async void has no task to wait for: before its first await the step returns to the synchronous caller
		var asyncVoidStop = await debugProtocolHost.WithContinueRequest().WaitForStoppedEvent();
		asyncVoidStop.ReadStopInfo().line.Should().Be(30);
		var afterAsyncVoid = await debugProtocolHost.WithStepOutRequest(asyncVoidStop.ThreadId!.Value).WaitForStoppedEvent();
		afterAsyncVoid.Reason.Should().Be(StoppedEvent.ReasonValue.Step);
		var afterAsyncVoidInfo = afterAsyncVoid.ReadStopInfo();
		afterAsyncVoidInfo.filePath.Should().EndWith("AsyncStepOutClass.cs");
		afterAsyncVoidInfo.line.Should().Be(10);
	}
}
