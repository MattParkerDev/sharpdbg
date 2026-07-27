using AwesomeAssertions;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using SharpDbg.Cli.Tests.Helpers;
using SharpDbg.Infrastructure.Debugger;

namespace SharpDbg.Cli.Tests;

public class FunctionBreakpointTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task SharpDbgCli_SetFunctionBreakpoint_NoFQN_BreaksOnAllMatchingMethods()
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

		var result = debugProtocolHost.SendRequestSync(new SetFunctionBreakpointsRequest([new FunctionBreakpoint("SameNamedClass.Test")]));
		var breakpointId = result.Breakpoints.Single().Id!.Value;

		debugProtocolHost
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var breakpointEvent = await debugProtocolHost.WaitForEvent<BreakpointEvent>(debugEventTcs, s => s.Breakpoint.Verified && s.Breakpoint.Id == breakpointId);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(debugEventTcs);
		debugProtocolHost.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTraceResponse);
		var topFrame = stackTraceResponse.StackFrames.Single();

		topFrame.Source.Path.Should().EndWith("SameNamedClass.cs");
		topFrame.Line.Should().Be(6);
		topFrame.Column.Should().Be(3);
		topFrame.EndColumn.Should().Be(4);

		var stoppedEvent2 = await debugProtocolHost.WithContinueRequest().WaitForStoppedEvent(debugEventTcs);
		debugProtocolHost.WithStackTraceRequest(stoppedEvent2.ThreadId!.Value, out var stackTraceResponse2);
		var topFrame2 = stackTraceResponse2.StackFrames.Single();

		topFrame2.Source.Path.Should().EndWith("SameNamedClass2.cs");
		topFrame2.Line.Should().Be(6);
		topFrame2.Column.Should().Be(3);
		topFrame2.EndColumn.Should().Be(4);
	}

	[Fact]
	public async Task SharpDbgCli_SetFunctionBreakpoint_FQN_BreaksOnMatchingMethod()
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

		var result = debugProtocolHost.SendRequestSync(new SetFunctionBreakpointsRequest([new FunctionBreakpoint("DebuggableConsoleApp.MyClass.MyMethod")]));
		var breakpointId = result.Breakpoints.Single().Id!.Value;

		debugProtocolHost
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var breakpointEvent = await debugProtocolHost.WaitForEvent<BreakpointEvent>(debugEventTcs, s => s.Breakpoint.Verified && s.Breakpoint.Id == breakpointId);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(debugEventTcs);
		debugProtocolHost.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTraceResponse);
		var topFrame = stackTraceResponse.StackFrames.Single();
		topFrame.ShouldBeAtSourceLocation("MyClass.cs", 10, 10, 2, 3);
	}

	[Fact]
	public async Task SharpDbgCli_SetFunctionBreakpoint_GenericClass_BreaksOnMatchingMethod()
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

		var result = debugProtocolHost.SendRequestSync(new SetFunctionBreakpointsRequest([new FunctionBreakpoint("DebuggableConsoleApp.MyGenericClassContainingAnotherGenericClass<T, U>.MyNestedGenericClass<T, U>.Test")]));
		var breakpointId = result.Breakpoints.Single().Id!.Value;

		debugProtocolHost
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var breakpointEvent = await debugProtocolHost.WaitForEvent<BreakpointEvent>(debugEventTcs, s => s.Breakpoint.Verified && s.Breakpoint.Id == breakpointId);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(debugEventTcs);
		debugProtocolHost.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTraceResponse);
		var topFrame = stackTraceResponse.StackFrames.Single();
		topFrame.ShouldBeAtSourceLocation("MyClassContainingAnotherClass.cs", 18, 18, 3, 4);
	}

	[Fact]
	public async Task SharpDbgCli_SetFunctionBreakpoint_NoParametersSpecified_BreaksOnAllMethodOverloads()
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

		var result = debugProtocolHost.SendRequestSync(new SetFunctionBreakpointsRequest([new FunctionBreakpoint("DebuggableConsoleApp.OverloadedMethodsClass.OverloadedMethod")]));
		var breakpointId = result.Breakpoints.Single().Id!.Value;

		debugProtocolHost
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var breakpointEvent = await debugProtocolHost.WaitForEvent<BreakpointEvent>(debugEventTcs, s => s.Breakpoint.Verified && s.Breakpoint.Id == breakpointId);

		await AssertBreakpointsHit();

		// Also test that classless method resolution works
		var result2 = debugProtocolHost.SendRequestSync(new SetFunctionBreakpointsRequest([new FunctionBreakpoint("OverloadedMethod")]));
		debugProtocolHost.WithContinueRequest();

		await AssertBreakpointsHit();

		return;

		async Task AssertBreakpointsHit()
		{
			var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(debugEventTcs);
			debugProtocolHost.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTraceResponse);
			var topFrame = stackTraceResponse.StackFrames.Single();
			topFrame.ShouldBeAtSourceLocation("OverloadedMethodsClass.cs", 14, 14, 2, 3);

			var stoppedEvent2 = await debugProtocolHost.WithContinueRequest().WaitForStoppedEvent(debugEventTcs);
			debugProtocolHost.WithStackTraceRequest(stoppedEvent2.ThreadId!.Value, out var stackTraceResponse2);
			var topFrame2 = stackTraceResponse2.StackFrames.Single();
			topFrame2.ShouldBeAtSourceLocation("OverloadedMethodsClass.cs", 18, 18, 2, 3);

			var stoppedEvent3 = await debugProtocolHost.WithContinueRequest().WaitForStoppedEvent(debugEventTcs);
			debugProtocolHost.WithStackTraceRequest(stoppedEvent3.ThreadId!.Value, out var stackTraceResponse3);
			var topFrame3 = stackTraceResponse3.StackFrames.Single();
			topFrame3.ShouldBeAtSourceLocation("OverloadedMethodsClass.cs", 22, 22, 2, 3);

			var stoppedEvent4 = await debugProtocolHost.WithContinueRequest().WaitForStoppedEvent(debugEventTcs);
			debugProtocolHost.WithStackTraceRequest(stoppedEvent4.ThreadId!.Value, out var stackTraceResponse4);
			var topFrame4 = stackTraceResponse4.StackFrames.Single();
			topFrame4.ShouldBeAtSourceLocation("OverloadedMethodsClass.cs", 26, 26, 2, 3);

			var stoppedEvent5 = await debugProtocolHost.WithContinueRequest().WaitForStoppedEvent(debugEventTcs);
			debugProtocolHost.WithStackTraceRequest(stoppedEvent5.ThreadId!.Value, out var stackTraceResponse5);
			var topFrame5 = stackTraceResponse5.StackFrames.Single();
			topFrame5.ShouldBeAtSourceLocation("OverloadedMethodsClass.cs", 30, 30, 2, 3);
		}
	}

	[Fact]
	public async Task SharpDbgCli_SetFunctionBreakpoint_MethodParametersSpecified_BreaksOnSingleMatchingOverload()
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

		var result = debugProtocolHost.SendRequestSync(new SetFunctionBreakpointsRequest([new FunctionBreakpoint("DebuggableConsoleApp.OverloadedMethodsClass.OverloadedMethod(int, MyClass)")]));
		var breakpointId = result.Breakpoints.Single().Id!.Value;

		debugProtocolHost
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var breakpointEvent = await debugProtocolHost.WaitForEvent<BreakpointEvent>(debugEventTcs, s => s.Breakpoint.Verified && s.Breakpoint.Id == breakpointId);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(debugEventTcs);
		debugProtocolHost.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTraceResponse);
		var topFrame = stackTraceResponse.StackFrames.Single();
		topFrame.ShouldBeAtSourceLocation("OverloadedMethodsClass.cs", 30, 30, 2, 3);

		// Continuing lands us at the same breakpoint, since we only break on the single matching overload
		var stoppedEvent2 = await debugProtocolHost.WithContinueRequest().WaitForStoppedEvent(debugEventTcs);
		debugProtocolHost.WithStackTraceRequest(stoppedEvent2.ThreadId!.Value, out var stackTraceResponse2);
		var topFrame2 = stackTraceResponse2.StackFrames.Single();
		topFrame2.ShouldBeAtSourceLocation("OverloadedMethodsClass.cs", 30, 30, 2, 3);
	}
}

file static class Extensions
{
	extension(StackFrame frame)
	{
		public void ShouldBeAtSourceLocation(string fileName, int line, int endLine, int column, int endColumn)
		{
			frame.Source.Path.Should().EndWith(fileName);
			frame.Line.Should().Be(line);
			frame.EndLine.Should().Be(endLine);
			frame.Column.Should().Be(column);
			frame.EndColumn.Should().Be(endColumn);
		}
	}
}
