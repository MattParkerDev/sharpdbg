using AwesomeAssertions;
using SharpDbg.Cli.Tests.Helpers;

namespace SharpDbg.Cli.Tests;

public class ConditionalBreakpointTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task ConditionalBreakpoint_WithTrueCondition_Stops()
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
			.WithConditionalBreakpointsRequest(22, condition: "myInt == 4")
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(stoppedEventTcs);
		var stopInfo = stoppedEvent.ReadStopInfo();
		stopInfo.filePath.Should().EndWith("MyClass.cs");
		stopInfo.line.Should().Be(22);
	}

	[Fact]
	public async Task ConditionalBreakpoint_WithFalseCondition_DoesNotStop()
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
			.WithConditionalBreakpointsRequest(22, condition: "myInt == 999")
			.WithBreakpointsRequest(20, Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "MyClass.cs"))
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		// Should hit the unconditional breakpoint on line 20, not the conditional one on line 22
		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(stoppedEventTcs);
		var stopInfo = stoppedEvent.ReadStopInfo();
		stopInfo.filePath.Should().EndWith("MyClass.cs");
		stopInfo.line.Should().Be(20);
	}

	[Fact]
	public async Task HitCondition_EqualsN_StopsOnNthHit()
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
			.WithConditionalBreakpointsRequest(22, hitCondition: "==2")
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		// Should stop on 2nd hit, not 1st
		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(stoppedEventTcs);
		var stopInfo = stoppedEvent.ReadStopInfo();
		stopInfo.filePath.Should().EndWith("MyClass.cs");
		stopInfo.line.Should().Be(22);
	}

	[Fact]
	public async Task HitCondition_GreaterThanOrEqual_StopsAfterThreshold()
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
			.WithConditionalBreakpointsRequest(22, hitCondition: ">=2")
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		// First stop should be on 2nd iteration (hit count >= 2)
		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(stoppedEventTcs);
		var stopInfo = stoppedEvent.ReadStopInfo();
		stopInfo.filePath.Should().EndWith("MyClass.cs");
		stopInfo.line.Should().Be(22);

		// Continue - should stop again on 3rd iteration
		var stoppedEvent2 = await debugProtocolHost.WithContinueRequest().WaitForStoppedEvent(stoppedEventTcs);
		var stopInfo2 = stoppedEvent2.ReadStopInfo();
		stopInfo2.filePath.Should().EndWith("MyClass.cs");
		stopInfo2.line.Should().Be(22);
	}

	[Fact]
	public async Task HitCondition_Modulo_StopsEveryNthHit()
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
			.WithConditionalBreakpointsRequest(22, hitCondition: "%2")
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		// First stop should be on 2nd iteration (2 % 2 == 0)
		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(stoppedEventTcs);
		var stopInfo = stoppedEvent.ReadStopInfo();
		stopInfo.filePath.Should().EndWith("MyClass.cs");
		stopInfo.line.Should().Be(22);

		// Continue - should skip 3rd, stop on 4th (4 % 2 == 0)
		var stoppedEvent2 = await debugProtocolHost.WithContinueRequest().WaitForStoppedEvent(stoppedEventTcs);
		var stopInfo2 = stoppedEvent2.ReadStopInfo();
		stopInfo2.filePath.Should().EndWith("MyClass.cs");
		stopInfo2.line.Should().Be(22);
	}
}
