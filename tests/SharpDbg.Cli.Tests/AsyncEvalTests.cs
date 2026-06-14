using AwesomeAssertions;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using SharpDbg.Cli.Tests.Helpers;

namespace SharpDbg.Cli.Tests;

public class AsyncEvalTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task AsyncMethod_EvaluationRequest_Returns()
	{
		var startSuspended = true;

		var (debugProtocolHost, initializedEventTcs, debugEventTcs, adapter, p2) = TestHelper.GetRunningDebugProtocolHostInProc(testOutputHelper, startSuspended);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(p2.Id)
			.WaitForInitializedEvent(initializedEventTcs);

		var breakpointFilePath = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "AsyncMethodEvalClass.cs");
		debugProtocolHost
			.WithBreakpointsRequest([10, 12], breakpointFilePath)
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(debugEventTcs);
		stoppedEvent.ReadStopInfo().Should().Be((breakpointFilePath, 10, 3));
		debugProtocolHost
			.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTraceResponse)
			.WithScopesRequest(stackTraceResponse.StackFrames!.First().Id, out var scopesResponse);

		scopesResponse.Scopes.Should().HaveCount(1);
		var scope = scopesResponse.Scopes.Single();

		debugProtocolHost.WithVariablesRequest(scope.VariablesReference, out var variables);

		variables.Should().HaveCount(2);

		TestVariablesEval(stackTraceResponse.StackFrames!.First().Id);

		// Continue, so we hit the breakpoint after the async suspension point
		debugProtocolHost.WithContinueRequest();

		var stoppedEvent2 = await debugProtocolHost.WaitForStoppedEvent(debugEventTcs);
		stoppedEvent2.ReadStopInfo().Should().Be((breakpointFilePath, 12, 3));
		debugProtocolHost.WithStackTraceRequest(stoppedEvent2.ThreadId!.Value, out var stackTraceResponse2);

		TestVariablesEval(stackTraceResponse2.StackFrames!.First().Id);
		return;

		void TestVariablesEval(int stackFrameId)
		{
			debugProtocolHost.WithEvaluateRequest(stackFrameId, "localInt", out var evaluateResponse);
			evaluateResponse.Result.Should().Be("4");

			debugProtocolHost.WithEvaluateRequest(stackFrameId, "IntField", out var evaluateResponse2);
			evaluateResponse2.Result.Should().Be("10");
		}
	}
}
