using AwesomeAssertions;
using SharpDbg.DapProtocol;
using SharpDbg.Cli.Tests.Helpers;

namespace SharpDbg.Cli.Tests;

// Evaluations whose debuggee code misbehaves must still complete, and leave the session usable
public class EvalHazardsTests(ITestOutputHelper testOutputHelper)
{
	private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

	[Fact]
	public async Task EvaluateRequest_BreakpointHitInsideEvaluatedGetter_CompletesAndSessionStaysUsable()
	{
		var (debugProtocolHost, adapter, p2) = await StartAtHazards(breakpointInGetter: true);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;
		var (_, frameId) = await WaitForStopInTest(debugProtocolHost);

		(await EvaluateWithTimeout(debugProtocolHost, frameId, "hazards.GetterWithBreakpoint")).Should().Be("5");
		(await EvaluateWithTimeout(debugProtocolHost, frameId, "1 + 1")).Should().Be("2");
		debugProtocolHost.ReceivedEvents.OfType<StoppedEvent>().Should().HaveCount(1);

		// The getter's breakpoint is still live for the program itself; continuing returns to the stop in Test
		debugProtocolHost.WithContinueRequest();
		var stopped = await debugProtocolHost.WaitForStoppedEvent();
		stopped.Reason.Should().Be(StoppedEvent.ReasonValue.Breakpoint);
	}

	[Fact]
	public async Task EvaluateRequest_GetterThatNeverReturns_TimesOutAndSessionStaysUsable()
	{
		var (debugProtocolHost, adapter, p2) = await StartAtHazards(breakpointInGetter: false);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;
		var (_, frameId) = await WaitForStopInTest(debugProtocolHost);

		var result = await EvaluateWithTimeout(debugProtocolHost, frameId, "hazards.BlockingGetter");
		result.Should().Contain("timed out");
		(await EvaluateWithTimeout(debugProtocolHost, frameId, "1 + 1")).Should().Be("2");
	}

	[Fact]
	public async Task EvaluateRequest_EvaluationExitsTheDebuggee_CompletesWithError()
	{
		var (debugProtocolHost, adapter, p2) = await StartAtHazards(breakpointInGetter: false);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;
		var (_, frameId) = await WaitForStopInTest(debugProtocolHost);

		var result = await EvaluateWithTimeout(debugProtocolHost, frameId, "hazards.ExitProcess()");
		result.Should().Contain("exited");
		await debugProtocolHost.WaitForEvent<TerminatedEvent>();
	}

	private async Task<(DebugProtocolHost, IDisposable, System.Diagnostics.Process)> StartAtHazards(bool breakpointInGetter)
	{
		var startSuspended = true;
		var (debugProtocolHost, adapter, p2) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended);
		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(p2.Id)
			.WaitForInitializedEvent();
		var file = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "EvalHazardsClass.cs");
		debugProtocolHost
			.WithBreakpointsRequest(breakpointInGetter ? [12, 35] : [35], file)
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);
		return (debugProtocolHost, adapter, p2);
	}

	private static async Task<(int ThreadId, int FrameId)> WaitForStopInTest(DebugProtocolHost debugProtocolHost)
	{
		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
		var threadId = stoppedEvent.ThreadId!.Value;
		return (threadId, debugProtocolHost.GetTopStackFrame(threadId).Id);
	}

	private static async Task<string> EvaluateWithTimeout(DebugProtocolHost debugProtocolHost, int frameId, string expression)
	{
		return await Task.Run(() =>
		{
			try
			{
				debugProtocolHost.WithEvaluateRequest(frameId, expression, out var response);
				return response.Result;
			}
			catch (Exception ex)
			{
				return ex.Message;
			}
		}).WaitAsync(RequestTimeout, TestContext.Current.CancellationToken);
	}
}
