using AwesomeAssertions;
using SharpDbg.Cli.Tests.Helpers;
using SharpDbg.DapProtocol;

namespace SharpDbg.Cli.Tests;

public class SingleThreadExecutionTests(ITestOutputHelper testOutputHelper)
{
	private const string Debuggee = "ThreadingConsoleApp";
	private static readonly string SourcePath = Path.JoinFromGitRoot("tests", Debuggee, "Program.cs");
	private static readonly string ProgramPath = Path.JoinFromGitRoot("artifacts", "bin", Debuggee, "debug", $"{Debuggee}.dll");
	private const int CheckpointLine = 22;
	private const int SpinLine = 23;
	private const int AfterSpinLine = 24;
	private const int BackgroundSuspendedLine = 26;
	private const int BackgroundRanLine = 30;

	[Fact]
	public Task Continue_WithSingleThread_KeepsTheOtherThreadSuspended()
		=> WithStoppedAtCheckpoint(async (host, checkpoint) =>
		{
			var response = host.SendRequestSync(new ContinueArguments
			{
				ThreadId = checkpoint.ThreadId!.Value,
				SingleThread = true
			});
			response.AllThreadsContinued.Should().BeFalse();

			// The background thread never opens the gate, so the main thread spins until the timeout.
			var nextStop = await host.WaitForStoppedEvent();
			host.GetTopStackFrame(nextStop.ThreadId!.Value).Line.Should().Be(BackgroundSuspendedLine);
		});

	[Fact]
	public Task Continue_WithoutSingleThread_RunsTheOtherThread()
		=> WithStoppedAtCheckpoint(async (host, checkpoint) =>
		{
			var response = host.SendRequestSync(new ContinueArguments
			{
				ThreadId = checkpoint.ThreadId!.Value,
				SingleThread = false
			});
			response.AllThreadsContinued.Should().BeTrue();

			var nextStop = await host.WaitForStoppedEvent();
			host.GetTopStackFrame(nextStop.ThreadId!.Value).Line.Should().Be(BackgroundRanLine);
		});

	[Fact]
	public Task StepOver_WithSingleThread_KeepsTheOtherThreadSuspended()
		=> WithStoppedAtCheckpoint(async (host, checkpoint) =>
		{
			// Step over Gate.Set() to the SpinWait statement.
			host.SendRequestSync(new NextArguments
			{
				ThreadId = checkpoint.ThreadId!.Value,
				SingleThread = true
			});
			var gateStep = await host.WaitForStoppedEvent();
			host.GetTopStackFrame(gateStep.ThreadId!.Value).Line.Should().Be(SpinLine);

			// Step over the SpinWait call: with single-thread execution the background thread cannot set the flag,
			// so the spin times out and the local 'ran' stays false.
			host.SendRequestSync(new NextArguments
			{
				ThreadId = gateStep.ThreadId!.Value,
				SingleThread = true
			});
			var nextStop = await host.WaitForStoppedEvent();
			host.GetTopStackFrame(nextStop.ThreadId!.Value).Line.Should().Be(AfterSpinLine);

			host.WithContinueRequest(nextStop.ThreadId!.Value);
			var afterContinue = await host.WaitForStoppedEvent();
			host.GetTopStackFrame(afterContinue.ThreadId!.Value).Line.Should().Be(BackgroundSuspendedLine);
		});

	private async Task WithStoppedAtCheckpoint(Func<DebugProtocolHost, StoppedEvent, Task> body)
	{
		var (debugProtocolHost, adapter) = TestHelper.GetLaunchDebugProtocolHostInProc(testOutputHelper);
		using var _ = adapter;
		using var __ = debugProtocolHost;

		var capabilities = debugProtocolHost.SendRequestSync(DebugAdapterProcessHelper.GetInitializeRequest());
		capabilities.SupportsSingleThreadExecutionRequests.Should().BeTrue();

		await debugProtocolHost
			.WithLaunchRequest(ProgramPath, stopAtEntry: true, justMyCode: true)
			.WaitForInitializedEvent();
		debugProtocolHost
			.WithBreakpointsRequest([CheckpointLine, BackgroundSuspendedLine, BackgroundRanLine], SourcePath)
			.WithConfigurationDoneRequest();

		var entryStop = await debugProtocolHost.WaitForStoppedEvent();
		entryStop.Reason.Should().Be(StoppedEvent.ReasonValue.Entry);

		// Run to the checkpoint first, so the background thread exists and is waiting at its gate.
		debugProtocolHost.WithContinueRequest(entryStop.ThreadId!.Value);
		var checkpointStop = await debugProtocolHost.WaitForStoppedEvent();
		debugProtocolHost.GetTopStackFrame(checkpointStop.ThreadId!.Value).Line.Should().Be(CheckpointLine);

		await body(debugProtocolHost, checkpointStop);
	}
}
