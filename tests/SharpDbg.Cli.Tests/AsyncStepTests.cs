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

		// step out of an async await method: stops on the await's line once the await is done, as a step out of a synchronous call
		// stops on the call's line
		var stoppedEvent8 = await debugProtocolHost.WithStepOutRequest(stoppedEvent7.ThreadId!.Value).WaitForStoppedEvent();
		var stopInfo8 = stoppedEvent8.ReadStopInfo();
		stopInfo8.filePath.Should().EndWith("MyAsyncClass.cs");
		stopInfo8.line.Should().Be(13);

		// step over the rest of the await's statement
		var stoppedEvent9 = await debugProtocolHost.WithStepOverRequest(stoppedEvent8.ThreadId!.Value).WaitForStoppedEvent();
		var stopInfo9 = stoppedEvent9.ReadStopInfo();
		stopInfo9.filePath.Should().EndWith("MyAsyncClass.cs");
		stopInfo9.line.Should().Be(14);

		// step into async void method
		var stoppedEvent10 = await debugProtocolHost.WithStepInRequest(stoppedEvent9.ThreadId!.Value).WaitForStoppedEvent();
		var stopInfo10 = stoppedEvent10.ReadStopInfo();
		stopInfo10.filePath.Should().EndWith("AnotherClass.cs");
		stopInfo10.line.Should().Be(25);

		// step out of async void method
		var stoppedEvent11 = await debugProtocolHost.WithStepOutRequest(stoppedEvent10.ThreadId!.Value).WaitForStoppedEvent();
		var stopInfo11 = stoppedEvent11.ReadStopInfo();
		stopInfo11.filePath.Should().EndWith("MyAsyncClass.cs");
		stopInfo11.line.Should().Be(14);
	}

	// An async step-out waits for the method's task and stops in the awaiting caller, on the await's line once the await is done, as a
	// step out of a synchronous call stops on the call's line. Without Just My Code the step must still land there, not in the builder or
	// the thread pool code that resumes the caller
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
		afterValueTask.ReadStopInfo().line.Should().Be(8);

		var taskStop = await debugProtocolHost.WithContinueRequest().WaitForStoppedEvent();
		taskStop.ReadStopInfo().line.Should().Be(24);
		var afterTask = await debugProtocolHost.WithStepOutRequest(taskStop.ThreadId!.Value).WaitForStoppedEvent();
		afterTask.Reason.Should().Be(StoppedEvent.ReasonValue.Step);
		afterTask.ReadStopInfo().line.Should().Be(9);

		// async void has no task to wait for: before its first await the step returns to the synchronous caller
		var asyncVoidStop = await debugProtocolHost.WithContinueRequest().WaitForStoppedEvent();
		asyncVoidStop.ReadStopInfo().line.Should().Be(30);
		var afterAsyncVoid = await debugProtocolHost.WithStepOutRequest(asyncVoidStop.ThreadId!.Value).WaitForStoppedEvent();
		afterAsyncVoid.Reason.Should().Be(StoppedEvent.ReasonValue.Step);
		var afterAsyncVoidInfo = afterAsyncVoid.ReadStopInfo();
		afterAsyncVoidInfo.filePath.Should().EndWith("AsyncStepOutClass.cs");
		afterAsyncVoidInfo.line.Should().Be(10);
	}

	// Once an async void method has resumed after an await, the thread pool called it: leaving it has no caller to stop in, so the
	// step runs free and the next stop is the breakpoint in the program's next loop iteration
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task SharpDbgCli_StepRequests_LeavingResumedAsyncVoidMethod_RunFree(bool justMyCode)
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
			.WithBreakpointsRequest(32, filePath)
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var resumedStop = await debugProtocolHost.WaitForStoppedEvent();
		resumedStop.ReadStopInfo().line.Should().Be(32);

		var afterStepOut = await debugProtocolHost.WithStepOutRequest(resumedStop.ThreadId!.Value).WaitForStoppedEvent();
		afterStepOut.Reason.Should().Be(StoppedEvent.ReasonValue.Breakpoint);
		afterStepOut.ReadStopInfo().line.Should().Be(32);

		var closingBrace = await debugProtocolHost.WithStepOverRequest(afterStepOut.ThreadId!.Value).WaitForStoppedEvent();
		closingBrace.Reason.Should().Be(StoppedEvent.ReasonValue.Step);
		closingBrace.ReadStopInfo().line.Should().Be(33);

		var afterStepOver = await debugProtocolHost.WithStepOverRequest(closingBrace.ThreadId!.Value).WaitForStoppedEvent();
		afterStepOver.Reason.Should().Be(StoppedEvent.ReasonValue.Breakpoint);
		afterStepOver.ReadStopInfo().line.Should().Be(32);
	}

	// Until its first yield an async method is an ordinary call: stepping out returns to the line that called it, even though nothing
	// awaits the task there. Once it has resumed after an await, the step waits for the task and stops after the caller's await
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task SharpDbgCli_StepOutRequest_OfAsyncMethodWhoseTaskIsAwaitedLater_StopsInTheCaller(bool justMyCode)
	{
		var startSuspended = true;
		var (debugProtocolHost, adapter, p2) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;

		var filePath = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "AsyncUnawaitedTaskClass.cs");
		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(p2.Id, justMyCode)
			.WaitForInitializedEvent();
		debugProtocolHost
			.WithBreakpointsRequest(16, filePath)
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var beforeYield = await debugProtocolHost.WaitForStoppedEvent();
		beforeYield.ReadStopInfo().line.Should().Be(16);
		var callLine = await debugProtocolHost.WithStepOutRequest(beforeYield.ThreadId!.Value).WaitForStoppedEvent();
		callLine.Reason.Should().Be(StoppedEvent.ReasonValue.Step);
		callLine.ThreadId.Should().Be(beforeYield.ThreadId);
		callLine.ReadStopInfo().line.Should().Be(8);
		var nextLine = await debugProtocolHost.WithStepOverRequest(callLine.ThreadId!.Value).WaitForStoppedEvent();
		nextLine.ReadStopInfo().line.Should().Be(9);

		// Only now break on the await: set earlier, it would interrupt the step out, which runs the method up to its first yield
		var atAwait = await debugProtocolHost
			.WithBreakpointsRequest(17, filePath)
			.WithContinueRequest()
			.WaitForStoppedEvent();
		atAwait.ReadStopInfo().line.Should().Be(17);
		var resumed = await debugProtocolHost.WithStepOverRequest(atAwait.ThreadId!.Value).WaitForStoppedEvent();
		resumed.ReadStopInfo().line.Should().Be(18);
		var afterCallerAwait = await debugProtocolHost.WithStepOutRequest(resumed.ThreadId!.Value).WaitForStoppedEvent();
		afterCallerAwait.Reason.Should().Be(StoppedEvent.ReasonValue.Step);
		// On the caller's await of the task, once it is done
		afterCallerAwait.ReadStopInfo().line.Should().Be(10);
	}

	// Stepping into an async method lands on its first line. Without Just My Code it must not stop on the way in the state machine's
	// constructor (Object..ctor) or the builder's Create and Start, which have decompiled source
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task SharpDbgCli_StepInRequest_IntoAsyncMethod_StopsOnItsFirstLine(bool justMyCode)
	{
		var startSuspended = true;
		var (debugProtocolHost, adapter, p2) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;

		var filePath = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "AsyncUnawaitedTaskClass.cs");
		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(p2.Id, justMyCode)
			.WaitForInitializedEvent();
		debugProtocolHost
			.WithBreakpointsRequest(8, filePath)
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var callStop = await debugProtocolHost.WaitForStoppedEvent();
		callStop.ReadStopInfo().line.Should().Be(8);
		var methodStart = await debugProtocolHost.WithStepInRequest(callStop.ThreadId!.Value).WaitForStoppedEvent();
		methodStart.Reason.Should().Be(StoppedEvent.ReasonValue.Step);
		methodStart.ThreadId.Should().Be(callStop.ThreadId);
		var methodStartInfo = methodStart.ReadStopInfo();
		methodStartInfo.filePath.Should().EndWith("AsyncUnawaitedTaskClass.cs");
		methodStartInfo.line.Should().Be(15);
		var firstStatement = await debugProtocolHost.WithStepOverRequest(methodStart.ThreadId!.Value).WaitForStoppedEvent();
		firstStatement.ReadStopInfo().line.Should().Be(16);
	}

	// A 'break' jumps over the loop's own MoveNextAsync await to the hidden DisposeAsync, whose await yields: the step is carried across
	// that await to the statement after the loop, not into the enumerator's finally or the framework code that resumes it
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task SharpDbgCli_StepOverRequest_OfBreakInAwaitForeach_StopsAfterTheLoop(bool justMyCode)
	{
		var startSuspended = true;
		var (debugProtocolHost, adapter, p2) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;

		var filePath = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "AsyncForeachBreakClass.cs");
		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(p2.Id, justMyCode)
			.WaitForInitializedEvent();
		debugProtocolHost
			.WithBreakpointsRequest(11, filePath)
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var breakStop = await debugProtocolHost.WaitForStoppedEvent();
		breakStop.ReadStopInfo().line.Should().Be(11);
		debugProtocolHost.WithClearBreakpointsRequest(filePath);

		var afterLoop = await debugProtocolHost.WithStepOverRequest(breakStop.ThreadId!.Value).WaitForStoppedEvent();
		afterLoop.Reason.Should().Be(StoppedEvent.ReasonValue.Step);
		var afterLoopInfo = afterLoop.ReadStopInfo();
		afterLoopInfo.filePath.Should().EndWith("AsyncForeachBreakClass.cs");
		afterLoopInfo.line.Should().Be(13);
	}

	// Stepping over a recursive await: the inner activations hit the same await first, the step must end in the one it started in
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task SharpDbgCli_StepOverRequest_OfRecursiveAwait_StaysInTheSameActivation(bool justMyCode)
	{
		var startSuspended = true;
		var (debugProtocolHost, adapter, p2) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;

		var filePath = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "AsyncRecursionClass.cs");
		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(p2.Id, justMyCode)
			.WaitForInitializedEvent();
		debugProtocolHost
			.WithBreakpointsRequest(16, filePath)
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var recurseStop = await debugProtocolHost.WaitForStoppedEvent();
		recurseStop.ReadStopInfo().line.Should().Be(16);
		debugProtocolHost.WithStackTraceRequest(recurseStop.ThreadId!.Value, out var startStack);
		debugProtocolHost.Evaluate(startStack.StackFrames!.First().Id, "n").Result.Should().Be("3", "the outermost activation reaches the await first");
		debugProtocolHost.WithClearBreakpointsRequest(filePath);

		var afterAwait = await debugProtocolHost.WithStepOverRequest(recurseStop.ThreadId!.Value).WaitForStoppedEvent();
		afterAwait.Reason.Should().Be(StoppedEvent.ReasonValue.Step);
		afterAwait.ReadStopInfo().line.Should().Be(17);
		debugProtocolHost.WithStackTraceRequest(afterAwait.ThreadId!.Value, out var endStack);
		debugProtocolHost.Evaluate(endStack.StackFrames!.First().Id, "n").Result.Should().Be("3");
	}

	// A step out of an awaited method stops on the await's line once the await is done, as a step out of a synchronous call stops on the
	// call's line, so the rest of the statement can still be stepped into: here the call the result is passed to. Whether the awaited
	// method completed at once or suspended
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task SharpDbgCli_StepOutRequest_OfAwaitedMethod_StopsOnTheAwaitLine_ThenStepsIntoTheRestOfTheStatement(bool justMyCode)
	{
		var startSuspended = true;
		var (debugProtocolHost, adapter, p2) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;

		var filePath = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "AsyncNestedAwaitClass.cs");
		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(p2.Id, justMyCode)
			.WaitForInitializedEvent();
		debugProtocolHost
			.WithBreakpointsRequest([21, 28], filePath)
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		// Done at once: the caller never left the stack
		var doneAtOnceStop = await debugProtocolHost.WaitForStoppedEvent();
		doneAtOnceStop.ReadStopInfo().line.Should().Be(21);
		var onAwaitLine = await debugProtocolHost.WithStepOutRequest(doneAtOnceStop.ThreadId!.Value).WaitForStoppedEvent();
		onAwaitLine.Reason.Should().Be(StoppedEvent.ReasonValue.Step);
		onAwaitLine.ThreadId.Should().Be(doneAtOnceStop.ThreadId);
		onAwaitLine.ReadStopInfo().line.Should().Be(9);
		var inProcess = await debugProtocolHost.WithStepInRequest(onAwaitLine.ThreadId!.Value).WaitForStoppedEvent();
		inProcess.Reason.Should().Be(StoppedEvent.ReasonValue.Step);
		var inProcessInfo = inProcess.ReadStopInfo();
		inProcessInfo.filePath.Should().EndWith("AsyncNestedAwaitClass.cs");
		inProcessInfo.line.Should().Be(15);

		// Suspended: the caller goes on after the await once the delay is done, on whichever thread
		var suspendsStop = await debugProtocolHost.WithContinueRequest().WaitForStoppedEvent();
		suspendsStop.ReadStopInfo().line.Should().Be(28);
		var onResumedAwaitLine = await debugProtocolHost.WithStepOutRequest(suspendsStop.ThreadId!.Value).WaitForStoppedEvent();
		onResumedAwaitLine.Reason.Should().Be(StoppedEvent.ReasonValue.Step);
		onResumedAwaitLine.ReadStopInfo().line.Should().Be(10);
		var inProcessAgain = await debugProtocolHost.WithStepInRequest(onResumedAwaitLine.ThreadId!.Value).WaitForStoppedEvent();
		inProcessAgain.Reason.Should().Be(StoppedEvent.ReasonValue.Step);
		var inProcessAgainInfo = inProcessAgain.ReadStopInfo();
		inProcessAgainInfo.filePath.Should().EndWith("AsyncNestedAwaitClass.cs");
		inProcessAgainInfo.line.Should().Be(15);
	}

	// A step out of an awaited method that has resumed after its own await: on the caller's await line, then a step over runs the rest
	// of the statement and stops on the next line
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task SharpDbgCli_StepOutRequest_OfResumedAwaitedMethod_StopsOnTheAwaitLine_ThenStepsOverTheRestOfTheStatement(bool justMyCode)
	{
		var startSuspended = true;
		var (debugProtocolHost, adapter, p2) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;

		var filePath = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "AsyncNestedAwaitClass.cs");
		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(p2.Id, justMyCode)
			.WaitForInitializedEvent();
		debugProtocolHost
			.WithBreakpointsRequest(30, filePath)
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var resumedStop = await debugProtocolHost.WaitForStoppedEvent();
		resumedStop.ReadStopInfo().line.Should().Be(30);
		debugProtocolHost.WithClearBreakpointsRequest(filePath);
		var onAwaitLine = await debugProtocolHost.WithStepOutRequest(resumedStop.ThreadId!.Value).WaitForStoppedEvent();
		onAwaitLine.Reason.Should().Be(StoppedEvent.ReasonValue.Step);
		onAwaitLine.ReadStopInfo().line.Should().Be(10);
		var nextLine = await debugProtocolHost.WithStepOverRequest(onAwaitLine.ThreadId!.Value).WaitForStoppedEvent();
		nextLine.Reason.Should().Be(StoppedEvent.ReasonValue.Step);
		nextLine.ReadStopInfo().line.Should().Be(11);
	}

	// Stepping over an await in one of two runs of the same method started side by side: the other run, which suspended for less time,
	// goes on after the await first, and the step must let it run on and end in the run it started in
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task SharpDbgCli_StepOverRequest_OfAwaitInConcurrentRuns_StaysInTheSameRun(bool justMyCode)
	{
		var startSuspended = true;
		var (debugProtocolHost, adapter, p2) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;

		var filePath = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "AsyncFanOutClass.cs");
		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(p2.Id, justMyCode)
			.WaitForInitializedEvent();
		debugProtocolHost
			.WithBreakpointsRequest(15, filePath)
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var awaitStop = await debugProtocolHost.WaitForStoppedEvent();
		awaitStop.ReadStopInfo().line.Should().Be(15);
		debugProtocolHost.WithStackTraceRequest(awaitStop.ThreadId!.Value, out var startStack);
		debugProtocolHost.Evaluate(startStack.StackFrames!.First().Id, "id").Result.Should().Be("1", "the first run reaches the await first");
		debugProtocolHost.WithClearBreakpointsRequest(filePath);

		var afterAwait = await debugProtocolHost.WithStepOverRequest(awaitStop.ThreadId!.Value).WaitForStoppedEvent();
		afterAwait.Reason.Should().Be(StoppedEvent.ReasonValue.Step);
		afterAwait.ReadStopInfo().line.Should().Be(16);
		debugProtocolHost.WithStackTraceRequest(afterAwait.ThreadId!.Value, out var endStack);
		debugProtocolHost.Evaluate(endStack.StackFrames!.First().Id, "id").Result.Should().Be("1", "the second run goes on after the await first, but is not the step's");
	}
}
