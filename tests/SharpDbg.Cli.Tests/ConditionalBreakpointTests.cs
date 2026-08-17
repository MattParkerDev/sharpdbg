using AwesomeAssertions;
using SharpDbg.Cli.Tests.Helpers;
using SharpDbg.Infrastructure.Debugger;

namespace SharpDbg.Cli.Tests;

public class ConditionalBreakpointTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task ConditionalBreakpoint_WithLambda_IsRejected()
	{
		var startSuspended = true;
		var (debugProtocolHost, initializedEventTcs, _, adapter, p2) = TestHelper.GetRunningDebugProtocolHostInProc(testOutputHelper, startSuspended);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(p2.Id)
			.WaitForInitializedEvent(initializedEventTcs);

		debugProtocolHost.WithBreakpointsRequest(
			Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "MyClass.cs"),
			[new SharpDbgBreakpointRequest(15, "_intList.Any(value => value == 4)")],
			out var response);

		var breakpoint = response.Breakpoints.Should().ContainSingle().Subject;
		breakpoint.Verified.Should().BeFalse();
		breakpoint.Message.Should().Be("Lambda expressions are not supported in breakpoint conditions.");
	}

	[Fact]
	public async Task ConditionalBreakpoint_WithTrueCondition_Stops()
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
			.WithBreakpointsRequest(Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "MyClass.cs"), [new SharpDbgBreakpointRequest(15, "myInt == 4")])
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(debugEventTcs);
		var stopInfo = stoppedEvent.ReadStopInfo();
		stopInfo.filePath.Should().EndWith("MyClass.cs");
		stopInfo.line.Should().Be(15);
	}

	[Fact]
	public async Task ConditionalBreakpoint_WithFalseCondition_DoesNotStop()
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
			.WithBreakpointsRequest(Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "MyClass.cs"),
				[
					new SharpDbgBreakpointRequest(15, "myInt == 999"),
					new SharpDbgBreakpointRequest(22)
				])
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		// Should skip the conditional breakpoint on line 15 (false condition) and hit the unconditional one on line 22
		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(debugEventTcs);
		var stopInfo = stoppedEvent.ReadStopInfo();
		stopInfo.filePath.Should().EndWith("MyClass.cs");
		stopInfo.line.Should().Be(22);
	}

	[Fact]
	public async Task HitCondition_EqualsN_StopsOnNthHit()
	{
		var startSuspended = true;
		var hitConditionFilePath = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "HitConditionClass.cs");

		var (debugProtocolHost, initializedEventTcs, debugEventTcs, adapter, p2) = TestHelper.GetRunningDebugProtocolHostInProc(testOutputHelper, startSuspended);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(p2.Id)
			.WaitForInitializedEvent(initializedEventTcs);

		debugProtocolHost
			.WithBreakpointsRequest(hitConditionFilePath, [new SharpDbgBreakpointRequest(10, HitCondition: "==2")])
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		// Should stop on 2nd hit, not 1st
		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(debugEventTcs);
		var stopInfo = stoppedEvent.ReadStopInfo();
		stopInfo.filePath.Should().EndWith("HitConditionClass.cs");
		stopInfo.line.Should().Be(10);

		debugProtocolHost.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTraceResponse);
		var stackFrameId = stackTraceResponse.StackFrames!.First().Id;
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "_count", out var evaluateResponse);
		evaluateResponse.Result.Should().Be("2");
	}

	[Fact]
	public async Task HitCondition_GreaterThanOrEqual_StopsAfterThreshold()
	{
		var startSuspended = true;
		var hitConditionFilePath = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "HitConditionClass.cs");

		var (debugProtocolHost, initializedEventTcs, debugEventTcs, adapter, p2) = TestHelper.GetRunningDebugProtocolHostInProc(testOutputHelper, startSuspended);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(p2.Id)
			.WaitForInitializedEvent(initializedEventTcs);

		debugProtocolHost
			.WithBreakpointsRequest(hitConditionFilePath, [new SharpDbgBreakpointRequest(10, HitCondition: ">=2")])
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		// First stop should be on 2nd iteration (hit count >= 2)
		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(debugEventTcs);
		var stopInfo = stoppedEvent.ReadStopInfo();
		stopInfo.filePath.Should().EndWith("HitConditionClass.cs");
		stopInfo.line.Should().Be(10);

		debugProtocolHost.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTraceResponse);
		var stackFrameId = stackTraceResponse.StackFrames!.First().Id;
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "_count", out var evaluateResponse);
		evaluateResponse.Result.Should().Be("2");

		// Continue - should stop again on 3rd iteration
		var stoppedEvent2 = await debugProtocolHost.WithContinueRequest().WaitForStoppedEvent(debugEventTcs);
		var stopInfo2 = stoppedEvent2.ReadStopInfo();
		stopInfo2.filePath.Should().EndWith("HitConditionClass.cs");
		stopInfo2.line.Should().Be(10);

		debugProtocolHost.WithStackTraceRequest(stoppedEvent2.ThreadId!.Value, out var stackTraceResponse2);
		var stackFrameId2 = stackTraceResponse2.StackFrames!.First().Id;
		debugProtocolHost.WithEvaluateRequest(stackFrameId2, "_count", out var evaluateResponse2);
		evaluateResponse2.Result.Should().Be("3");
	}

	[Fact]
	public async Task HitCondition_Modulo_StopsEveryNthHit()
	{
		var startSuspended = true;
		var hitConditionFilePath = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "HitConditionClass.cs");

		var (debugProtocolHost, initializedEventTcs, debugEventTcs, adapter, p2) = TestHelper.GetRunningDebugProtocolHostInProc(testOutputHelper, startSuspended);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(p2.Id)
			.WaitForInitializedEvent(initializedEventTcs);

		debugProtocolHost
			.WithBreakpointsRequest(hitConditionFilePath, [new SharpDbgBreakpointRequest(10, HitCondition: "%2")])
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		// First stop should be on 2nd iteration (2 % 2 == 0)
		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(debugEventTcs);
		var stopInfo = stoppedEvent.ReadStopInfo();
		stopInfo.filePath.Should().EndWith("HitConditionClass.cs");
		stopInfo.line.Should().Be(10);

		debugProtocolHost.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTraceResponse);
		var stackFrameId = stackTraceResponse.StackFrames!.First().Id;
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "_count", out var evaluateResponse);
		evaluateResponse.Result.Should().Be("2");

		// Continue - should skip 3rd, stop on 4th (4 % 2 == 0)
		var stoppedEvent2 = await debugProtocolHost.WithContinueRequest().WaitForStoppedEvent(debugEventTcs);
		var stopInfo2 = stoppedEvent2.ReadStopInfo();
		stopInfo2.filePath.Should().EndWith("HitConditionClass.cs");
		stopInfo2.line.Should().Be(10);

		debugProtocolHost.WithStackTraceRequest(stoppedEvent2.ThreadId!.Value, out var stackTraceResponse2);
		var stackFrameId2 = stackTraceResponse2.StackFrames!.First().Id;
		debugProtocolHost.WithEvaluateRequest(stackFrameId2, "_count", out var evaluateResponse2);
		evaluateResponse2.Result.Should().Be("4");
	}
}
