using AwesomeAssertions;
using SharpDbg.Cli.Tests.Helpers;

namespace SharpDbg.Cli.Tests;

public class LambdaStepTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task SharpDbgCli_StepRequests_InLambda_Returns_StoppedEventsAtCorrectLocation()
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
		debugProtocolHost
			.WithBreakpointsRequest([21, 34], Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "Lambdas", "MyLambdaClass.cs"))
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		// we should not stop at line 21, as it is within the lambda, and it has not been invoked yet
		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(debugEventTcs);
		var stopInfo = stoppedEvent.ReadStopInfo();
		stopInfo.filePath.Should().EndWith("MyLambdaClass.cs");
		stopInfo.line.Should().Be(34);

		// continue, and now we should stop at line 21 within the lambda
		var stoppedEvent2 = await debugProtocolHost.WithContinueRequest().WaitForStoppedEvent(debugEventTcs);
		var stopInfo2 = stoppedEvent2.ReadStopInfo();
		stopInfo2.filePath.Should().EndWith("MyLambdaClass.cs");
		stopInfo2.line.Should().Be(21);

		// Set breakpoint at lambda declaration
		debugProtocolHost.WithBreakpointsRequest(14, Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "Lambdas", "MyLambdaClass.cs"));

		// Continue (while loop), and we should hit the lambda declaration breakpoint
		var stoppedEvent3 = await debugProtocolHost.WithContinueRequest().WaitForStoppedEvent(debugEventTcs);
		var stopInfo3 = stoppedEvent3.ReadStopInfo();
		stopInfo3.filePath.Should().EndWith("MyLambdaClass.cs");
		stopInfo3.line.Should().Be(14);
	}
}
