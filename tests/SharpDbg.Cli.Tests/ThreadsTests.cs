using AwesomeAssertions;
using SharpDbg.DapProtocol;
using SharpDbg.Cli.Tests.Helpers;

namespace SharpDbg.Cli.Tests;

public class ThreadsTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task ThreadsRequest_Launch_ThreadRunningMainIsNamedMainThread()
	{
		var (host, adapter) = TestHelper.GetLaunchDebugProtocolHostInProc(testOutputHelper);
		using var _ = adapter;
		using var __ = host;
		var program = Path.JoinFromGitRoot("artifacts", "bin", "DebuggableConsoleApp", "debug", "DebuggableConsoleApp.dll");

		await host
			.WithInitializeRequest()
			.WithLaunchRequest(program, stopAtEntry: true, justMyCode: true)
			.WaitForInitializedEvent();
		host.WithConfigurationDoneRequest();

		var stoppedEvent = await host.WaitForStoppedEvent();
		stoppedEvent.Reason.Should().Be(StoppedEvent.ReasonValue.Entry);

		AssertOnlyStoppedThreadIsNamedMainThread(host, stoppedEvent.ThreadId!.Value);
	}

	// An attach announces the threads that already exist in the runtime's own order, which need not begin with the main thread
	[Fact]
	public async Task ThreadsRequest_Attach_ThreadRunningMainIsNamedMainThread()
	{
		var startSuspended = false;
		var (host, adapter, p2) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = host;

		await host
			.WithInitializeRequest()
			.WithAttachRequest(p2.Id, justMyCode: true)
			.WaitForInitializedEvent();
		host
			.WithBreakpointsRequest([12], Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "ClassWithBclCall.cs"))
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var stoppedEvent = await host.WaitForStoppedEvent();
		stoppedEvent.ReadStopInfo().filePath.Should().EndWith("ClassWithBclCall.cs");

		AssertOnlyStoppedThreadIsNamedMainThread(host, stoppedEvent.ThreadId!.Value);
	}

	private void AssertOnlyStoppedThreadIsNamedMainThread(DebugProtocolHost host, int stoppedThreadId)
	{
		var threads = host.SendRequestSync(new ThreadsArguments()).Threads;
		foreach (var thread in threads)
		{
			host.WithStackTraceRequest(thread.Id, out var stackTraceResponse, null);
			testOutputHelper.WriteLine($"Thread {thread.Id} '{thread.Name}': {stackTraceResponse.StackFrames.Count} frames");
		}

		threads.Single(t => t.Id == stoppedThreadId).Name.Should().Be("Main Thread");
		threads.Where(t => t.Name == "Main Thread").Should().ContainSingle();
	}
}
