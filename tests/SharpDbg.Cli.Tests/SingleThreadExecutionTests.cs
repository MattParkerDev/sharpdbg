using AwesomeAssertions;
using SharpDbg.Cli.Tests.Helpers;
using SharpDbg.DapProtocol;

namespace SharpDbg.Cli.Tests;

// SingleThreadExecutionClass's worker thread counts all the time it runs, so its count shows whether a request resumed it
public class SingleThreadExecutionTests(ITestOutputHelper testOutputHelper)
{
	private static readonly string SourcePath = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "SingleThreadExecutionClass.cs");
	private const int FirstLine = 16;
	private const int SecondLine = 17;
	private const int ThirdLine = 18;
	private const int IncrementCallLine = 19;
	private const int WaitForWorkerLine = 20;
	private const int StopWorkerLine = 21;
	private const int IncrementOpenBraceLine = 26;
	private const int AwaitDelayLine = 46;
	private const int AfterDelayLine = 47;
	private const int AfterThatLine = 48;
	private const int AfterJoinLine = 56;
	private const int ShortWorkerLine = 61;

	[Fact]
	public async Task StepOver_SingleThread_OverAnAwait_LetsTheAwaitResume()
	{
		var (host, adapter, p2, threadId) = await StartAtLine(AwaitDelayLine);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = host;

		// The delay's timer, and the thread the method resumes on, are other threads: they run while the await is pending
		host.WithStepOverRequest(threadId, singleThread: true);
		var stoppedEvent = await host.WaitForStoppedEvent();
		stoppedEvent.ReadStopInfo().line.Should().Be(AfterDelayLine);
		var continuedEvents = host.ReceivedEvents.OfType<ContinuedEvent>().ToList();
		continuedEvents.First().AllThreadsContinued.Should().NotBe(true);
		continuedEvents.Should().Contain(e => e.AllThreadsContinued == true);

		var resumedThreadId = stoppedEvent.ThreadId!.Value;
		host.WithStepOverRequest(resumedThreadId, singleThread: true);
		var nextStop = await host.WaitForStoppedEvent();
		nextStop.ThreadId.Should().Be(resumedThreadId);
		nextStop.ReadStopInfo().line.Should().Be(AfterThatLine);
		host.ReceivedEvents.OfType<ContinuedEvent>().Last().AllThreadsContinued.Should().NotBe(true);
	}

	[Fact]
	public async Task ContinueRequest_SingleThread_ResumesTheOtherThreadsWhenTheThreadExits()
	{
		var (host, adapter, p2, workerThreadId) = await StartAtLine(ShortWorkerLine, AfterJoinLine);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = host;

		var continueResponse = host.SendRequestSync(new ContinueArguments { ThreadId = workerThreadId, SingleThread = true });
		continueResponse.AllThreadsContinued.Should().NotBe(true);

		// The test thread only gets past Join once the worker has exited, and only runs if the exit resumed it
		var stoppedEvent = await host.WaitForStoppedEvent();
		stoppedEvent.ThreadId.Should().NotBe(workerThreadId);
		stoppedEvent.ReadStopInfo().line.Should().Be(AfterJoinLine);
		host.ReceivedEvents.OfType<ContinuedEvent>().Last().AllThreadsContinued.Should().BeTrue();
	}

	[Fact]
	public async Task Initialize_AdvertisesSingleThreadExecutionRequests()
	{
		var (host, adapter, p2) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended: true);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = host;

		var capabilities = host.SendRequestSync(DebugAdapterProcessHelper.GetInitializeRequest());
		capabilities.SupportsSingleThreadExecutionRequests.Should().BeTrue();
	}

	[Fact]
	public async Task StepRequests_SingleThread_LeaveOtherThreadsFrozen()
	{
		var (host, adapter, p2, threadId) = await StartAtLine(FirstLine);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = host;

		var count = ReadWorkerCounter(host, threadId);

		await StepAndExpect(() => host.WithStepOverRequest(threadId, singleThread: true), SecondLine);
		await StepAndExpect(() => host.WithStepOverRequest(threadId, singleThread: true), ThirdLine);
		await StepAndExpect(() => host.WithStepOverRequest(threadId, singleThread: true), IncrementCallLine);
		await StepAndExpect(() => host.WithStepInRequest(threadId, singleThread: true), IncrementOpenBraceLine);
		await StepAndExpect(() => host.WithStepOutRequest(threadId, singleThread: true), IncrementCallLine);

		host.ReceivedEvents.OfType<ContinuedEvent>().Should().HaveCount(5)
			.And.AllSatisfy(e =>
			{
				e.ThreadId.Should().Be(threadId);
				e.AllThreadsContinued.Should().NotBe(true);
			});

		// The step over WaitForWorkerToCount only ends once the worker has counted
		await StepAndExpect(() => host.WithStepOverRequest(threadId), WaitForWorkerLine, workerShouldRun: null);
		await StepAndExpect(() => host.WithStepOverRequest(threadId), StopWorkerLine, workerShouldRun: true);
		host.ReceivedEvents.OfType<ContinuedEvent>().TakeLast(2).Should().AllSatisfy(e => e.AllThreadsContinued.Should().BeTrue());

		// workerShouldRun null: the worker may or may not have been scheduled while the thread stepped
		async Task StepAndExpect(Action step, int line, bool? workerShouldRun = false)
		{
			step();
			var stoppedEvent = await host.WaitForStoppedEvent();
			stoppedEvent.ThreadId.Should().Be(threadId);
			stoppedEvent.ReadStopInfo().line.Should().Be(line);
			var newCount = ReadWorkerCounter(host, threadId);
			testOutputHelper.WriteLine($"Stopped at line {line}, worker count {count} -> {newCount}");
			if (workerShouldRun is true) newCount.Should().BeGreaterThan(count);
			else if (workerShouldRun is false) newCount.Should().Be(count);
			count = newCount;
		}
	}

	[Fact]
	public async Task ContinueRequest_SingleThread_LeavesOtherThreadsFrozen()
	{
		var (host, adapter, p2, threadId) = await StartAtLine(FirstLine, ThirdLine);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = host;

		var count = ReadWorkerCounter(host, threadId);

		var continueResponse = host.SendRequestSync(new ContinueArguments { ThreadId = threadId, SingleThread = true });
		continueResponse.AllThreadsContinued.Should().NotBe(true);
		var stoppedEvent = await host.WaitForStoppedEvent();
		stoppedEvent.ThreadId.Should().Be(threadId);
		stoppedEvent.ReadStopInfo().line.Should().Be(ThirdLine);
		ReadWorkerCounter(host, threadId).Should().Be(count);

		var continuedEvent = host.ReceivedEvents.OfType<ContinuedEvent>().Should().ContainSingle().Subject;
		continuedEvent.ThreadId.Should().Be(threadId);
		continuedEvent.AllThreadsContinued.Should().NotBe(true);
	}

	[Fact]
	public async Task ContinueRequest_NotSingleThread_ResumesThreadsFrozenByAnEarlierSingleThreadStep()
	{
		var (host, adapter, p2, threadId) = await StartAtLine(FirstLine);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = host;

		var count = ReadWorkerCounter(host, threadId);
		host.WithStepOverRequest(threadId, singleThread: true);
		(await host.WaitForStoppedEvent()).ReadStopInfo().line.Should().Be(SecondLine);
		ReadWorkerCounter(host, threadId).Should().Be(count);

		// The thread only gets past WaitForWorkerToCount to the breakpoint once the worker has counted
		host.WithBreakpointsRequest([StopWorkerLine], SourcePath);
		var continueResponse = host.SendRequestSync(new ContinueArguments { ThreadId = threadId });
		continueResponse.AllThreadsContinued.Should().NotBe(false);
		(await host.WaitForStoppedEvent()).ReadStopInfo().line.Should().Be(StopWorkerLine);
		ReadWorkerCounter(host, threadId).Should().BeGreaterThan(count);
	}

	private async Task<(DebugProtocolHost Host, IDisposable Adapter, System.Diagnostics.Process Debuggee, int ThreadId)> StartAtLine(params int[] breakpointLines)
	{
		var startSuspended = true;
		var (host, adapter, p2) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended);

		await host
			.WithInitializeRequest()
			.WithAttachRequest(p2.Id)
			.WaitForInitializedEvent();
		host
			.WithBreakpointsRequest(breakpointLines, SourcePath)
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var stoppedEvent = await host.WaitForStoppedEvent();
		stoppedEvent.ReadStopInfo().line.Should().Be(breakpointLines[0]);
		var threadId = stoppedEvent.ThreadId!.Value;
		// Drop the first breakpoint, so a later step or continue cannot stop at it again
		host.WithBreakpointsRequest(breakpointLines[1..], SourcePath);
		return (host, adapter, p2, threadId);
	}

	private static int ReadWorkerCounter(DebugProtocolHost host, int threadId)
	{
		host.WithStackTraceRequest(threadId, out var stackTrace);
		return int.Parse(host.Evaluate(stackTrace.StackFrames[0].Id, "_workerCounter").Result);
	}
}
