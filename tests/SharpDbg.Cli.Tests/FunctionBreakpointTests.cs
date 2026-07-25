using AwesomeAssertions;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using SharpDbg.Cli.Tests.Helpers;

namespace SharpDbg.Cli.Tests;

public class FunctionBreakpointTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task SharpDbgCli_SetFunctionBreakpoint_HitsAndStopsAtTargetMethod()
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

		// Set function breakpoint BEFORE ConfigurationDone, same pattern as source breakpoints
		var response = debugProtocolHost.SendRequestSync(new SetFunctionBreakpointsRequest
		{
			Breakpoints = [new FunctionBreakpoint { Name = "DebuggableConsoleApp.FunctionBreakpointTarget.TargetMethod" }]
		});
		response.Breakpoints.Should().HaveCount(1);

		debugProtocolHost
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		// First BreakpointEvent: pending (Verified=false) from SendAllBreakpointEvents during attach
		var bpEvent1 = await debugProtocolHost.WaitForEvent<BreakpointEvent>(debugEventTcs,
			e => e.Breakpoint.Id == response.Breakpoints[0].Id);
		bpEvent1.Breakpoint.Verified.Should().BeFalse();

		// Second BreakpointEvent: verified (Verified=true) from TryBindPendingBreakpoints when modules load
		var bpEvent2 = await debugProtocolHost.WaitForEvent<BreakpointEvent>(debugEventTcs, e => e.Breakpoint.Verified);
		bpEvent2.Breakpoint.Verified.Should().BeTrue();
		bpEvent2.Breakpoint.Id.Should().Be(response.Breakpoints[0].Id);

		// Now the breakpoint should hit on the next iteration
		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(debugEventTcs);

		stoppedEvent.Reason.Should().Be(StoppedEvent.ReasonValue.FunctionBreakpoint);
		var stopInfo = stoppedEvent.ReadStopInfo();
		stopInfo.filePath.Should().EndWith("FunctionBreakpointTarget.cs");

		// Verify the function breakpoint stopped at the correct location
		debugProtocolHost.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackResponse);
		var frameId = stackResponse.StackFrames[0].Id;
		stackResponse.StackFrames[0].Name.Should().Contain("FunctionBreakpointTarget.TargetMethod");
		stackResponse.StackFrames[0].Source.Path.Should().EndWith("FunctionBreakpointTarget.cs");

		// Verify local variable exists (null at function entry, before assignment)
		debugProtocolHost.WithScopesRequest(frameId, out var scopes);
		var localsRef = scopes.Scopes.Single(s => s.Name == "Locals").VariablesReference;
		debugProtocolHost.WithVariablesRequest(localsRef, out var variables);
		variables.Should().Contain(v => v.Name == "localInTarget");
	}

	[Fact]
	public async Task SharpDbgCli_SetFunctionBreakpoint_ByClassAndMethodName_HitsCorrectly()
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

		// Just class name + method name (no namespace)
		var response = debugProtocolHost.SendRequestSync(new SetFunctionBreakpointsRequest
		{
			Breakpoints = [new FunctionBreakpoint { Name = "FunctionBreakpointTarget.TargetMethod" }]
		});
		response.Breakpoints.Should().HaveCount(1);

		debugProtocolHost
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		// Wait for pending then verified
		await debugProtocolHost.WaitForEvent<BreakpointEvent>(debugEventTcs,
			e => e.Breakpoint.Id == response.Breakpoints[0].Id && !e.Breakpoint.Verified);
		await debugProtocolHost.WaitForEvent<BreakpointEvent>(debugEventTcs, e => e.Breakpoint.Verified);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(debugEventTcs);
		stoppedEvent.Reason.Should().Be(StoppedEvent.ReasonValue.FunctionBreakpoint);
	}
}
