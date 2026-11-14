using AwesomeAssertions;
using SharpDbg.Cli.Tests.Helpers;
using SharpDbg.DapProtocol;
using SharpDbg.Infrastructure.Debugger;

namespace SharpDbg.Cli.Tests;

public class StepTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task ResumeRequests_SendContinuedEventBeforeTheNextStop()
	{
		var startSuspended = true;

		var (debugProtocolHost, adapter, p2) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(p2.Id)
			.WaitForInitializedEvent();
		debugProtocolHost
			.WithBreakpointsRequest(20)
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var threadId = (await debugProtocolHost.WaitForStoppedEvent()).ThreadId!.Value;

		// An evaluation runs the debuggee, but the client is not told it continued
		debugProtocolHost.WithStackTraceRequest(threadId, out var stackTrace);
		debugProtocolHost.Evaluate(stackTrace.StackFrames[0].Id, "AnotherClass.MyStaticMethod()");

		// WaitForEvent fails if the stop comes before the continued event
		debugProtocolHost.WithStepInRequest(threadId);
		await ExpectContinuedThenStop();
		debugProtocolHost.WithStepOutRequest(threadId);
		await ExpectContinuedThenStop();
		debugProtocolHost.WithStepOverRequest(threadId);
		await ExpectContinuedThenStop();

		// The false condition's hit is not reported, so it sends no continued event either
		debugProtocolHost.WithBreakpointsRequest(Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "MyClass.cs"), [new SharpDbgBreakpointRequest(20, "_counter == 1000"), new SharpDbgBreakpointRequest(21)], out var breakpointsResponse);
		breakpointsResponse.Breakpoints.Should().AllSatisfy(b => b.Verified.Should().BeTrue());
		debugProtocolHost.WithContinueRequest(threadId);
		await ExpectContinuedThenStop();

		debugProtocolHost.ReceivedEvents.OfType<ContinuedEvent>().Should().HaveCount(4)
			.And.AllSatisfy(e =>
			{
				e.ThreadId.Should().Be(threadId);
				e.AllThreadsContinued.Should().BeTrue();
			});

		async Task ExpectContinuedThenStop()
		{
			await debugProtocolHost.WaitForEvent<ContinuedEvent>();
			await debugProtocolHost.WaitForStoppedEvent();
		}
	}

	[Fact]
	public async Task SharpDbgCli_StepRequests_Returns_StoppedEventsAtCorrectLocation()
	{
		var startSuspended = true;

		var (debugProtocolHost, adapter, p2) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(p2.Id)
			.WaitForInitializedEvent();
		debugProtocolHost
			.WithBreakpointsRequest(20)
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
		var stopInfo = stoppedEvent.ReadStopInfo();
		stopInfo.filePath.Should().EndWith("MyClass.cs");
		stopInfo.line.Should().Be(20);

		var stoppedEvent2 = await debugProtocolHost
			.WithStepInRequest(stoppedEvent.ThreadId!.Value)
			.WaitForStoppedEvent();
		var stopInfo2 = stoppedEvent2.ReadStopInfo();
		stopInfo2.filePath.Should().EndWith("AnotherClass.cs");
		stopInfo2.line.Should().Be(7);

		var stoppedEvent3 = await debugProtocolHost
			.WithStepOutRequest(stoppedEvent.ThreadId!.Value)
			.WaitForStoppedEvent();
		var stopInfo3 = stoppedEvent3.ReadStopInfo();
		// Stepping out should land us back on the same line as the method we just stepped out of
		stopInfo3.filePath.Should().EndWith("MyClass.cs");
		stopInfo3.line.Should().Be(20);

		// Continue so we loop and are back at the breakpoint
		var stoppedEvent4 = await debugProtocolHost.WithContinueRequest().WaitForStoppedEvent();
		var stopInfo4 = stoppedEvent4.ReadStopInfo();
		stopInfo4.filePath.Should().EndWith("MyClass.cs");
		stopInfo4.line.Should().Be(20);

		// Now, put a breakpoint inside AnotherMethod, and step over. We should still be in AnotherClass.cs
		debugProtocolHost.WithBreakpointsRequest(8, Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "Namespace1", "AnotherClass.cs"));

		var stoppedEvent5 = await debugProtocolHost.WithStepOverRequest(stoppedEvent.ThreadId!.Value).WaitForStoppedEvent();
		var stopInfo5 = stoppedEvent5.ReadStopInfo();
		stopInfo5.filePath.Should().EndWith("AnotherClass.cs");
		stopInfo5.line.Should().Be(8);

		var stoppedEvent6 = await debugProtocolHost.WithStepOverRequest(stoppedEvent.ThreadId!.Value).WaitForStoppedEvent();
		var stopInfo6 = stoppedEvent6.ReadStopInfo();
		stopInfo6.filePath.Should().EndWith("AnotherClass.cs");
		stopInfo6.line.Should().Be(9);

		var stoppedEvent7 = await debugProtocolHost.WithContinueRequest().WaitForStoppedEvent();
		var stopInfo7 = stoppedEvent7.ReadStopInfo();
		stopInfo7.filePath.Should().EndWith("MyClass.cs");
		stopInfo7.line.Should().Be(20);

		// breakpoint on a line that would F11 into unmapped code
		debugProtocolHost.WithBreakpointsRequest(13, Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "Namespace1", "AnotherClass.cs"));
		var stoppedEvent8 = await debugProtocolHost.WithContinueRequest().WaitForStoppedEvent();
		var stopInfo8 = stoppedEvent8.ReadStopInfo();
		stopInfo8.filePath.Should().EndWith("AnotherClass.cs");
		stopInfo8.line.Should().Be(13);

		// ensure that we do not receive stop info with no source. StepIn with JMC false, landing in non-user code, continues and stops at the next user code
		var stoppedEvent9 = await debugProtocolHost.WithStepInRequest(stoppedEvent.ThreadId!.Value).WaitForStoppedEvent();
		var stopInfo9 = stoppedEvent9.ReadStopInfo();
		stopInfo9.filePath.Should().EndWith("AnotherClass.cs");
		stopInfo9.line.Should().Be(14);

		List<int> threadIds = [stoppedEvent.ThreadId!.Value, stoppedEvent2.ThreadId!.Value, stoppedEvent3.ThreadId!.Value, stoppedEvent4.ThreadId!.Value, stoppedEvent5.ThreadId!.Value, stoppedEvent6.ThreadId!.Value, stoppedEvent7.ThreadId!.Value, stoppedEvent8.ThreadId!.Value, stoppedEvent9.ThreadId!.Value];
		threadIds.Distinct().Should().HaveCount(1);
	}

	[Fact]
	public async Task SharpDbgCli_StepRequests_StepIntoModuleWithoutSymbols_Decompiles()
	{
		var startSuspended = true;

		var (debugProtocolHost, adapter, p2) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(p2.Id, false)
			.WaitForInitializedEvent();
		debugProtocolHost
			.WithBreakpointsRequest(15)
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
		var stopInfo = stoppedEvent.ReadStopInfo();
		stopInfo.filePath.Should().EndWith("MyClass.cs");
		stopInfo.line.Should().Be(15);

		var stoppedEvent2 = await debugProtocolHost.WithStepInRequest(stoppedEvent.ThreadId!.Value).WaitForStoppedEvent();
		var stopInfo2 = stoppedEvent2.ReadStopInfo();
		stopInfo2.filePath.Should().EndWith("Console.cs");
		// Bumping ICSharpCode.Decompiler changed the decompiled output, so the line number changed. If this is failing, clear temp/SharpIdeSymbolCache and try again.
		stopInfo2.line.Should().Be(1245);
	}

	[Fact]
	public async Task SharpDbgCli_Continue_StopsAtBreakpointOnMultilineSwitchExpression()
	{
		var startSuspended = true;

		var (debugProtocolHost, adapter, p2) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(p2.Id, false)
			.WaitForInitializedEvent();
		debugProtocolHost
			.WithBreakpointsRequest([7, 9], Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "MultilineSwitchInMethodCall.cs"))
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
		var stopInfo = stoppedEvent.ReadStopInfo();
		stopInfo.filePath.Should().EndWith("MultilineSwitchInMethodCall.cs");
		stopInfo.line.Should().Be(7);

		var stoppedEvent2 = await debugProtocolHost.WithContinueRequest().WaitForStoppedEvent();
		var stopInfo2 = stoppedEvent2.ReadStopInfo();
		stopInfo2.filePath.Should().EndWith("MultilineSwitchInMethodCall.cs");
		stopInfo2.line.Should().Be(9);
	}

	[Fact]
	// Testing behaviour that suppresses a breakpoint if a Step lands at the same location
	public async Task SharpDbgCli_StepOverToBreakpointedLine_StepOver_StopsAtNextLine()
	{
		var startSuspended = true;

		var (debugProtocolHost, adapter, p2) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(p2.Id, false)
			.WaitForInitializedEvent();
		debugProtocolHost
			.WithBreakpointsRequest([25, 26], Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "Program.cs"))
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
		var stopInfo = stoppedEvent.ReadStopInfo();
		stopInfo.filePath.Should().EndWith("Program.cs");
		stopInfo.line.Should().Be(25);

		var stoppedEvent2 = await debugProtocolHost.WithStepOverRequest(stoppedEvent.ThreadId!.Value).WaitForStoppedEvent();
		var stopInfo2 = stoppedEvent2.ReadStopInfo();
		stopInfo2.filePath.Should().EndWith("Program.cs");
		stopInfo2.line.Should().Be(26);

		var stoppedEvent3 = await debugProtocolHost.WithStepOverRequest(stoppedEvent.ThreadId!.Value).WaitForStoppedEvent();
		var stopInfo3 = stoppedEvent3.ReadStopInfo();
		stopInfo3.filePath.Should().EndWith("Program.cs");
		stopInfo3.line.Should().Be(27);
	}
}
