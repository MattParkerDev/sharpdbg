using AwesomeAssertions;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using SharpDbg.Cli.Tests.Helpers;

namespace SharpDbg.Cli.Tests;

public class BreakpointTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task SharpDbgCli_SetBreakpoint_RaisesBreakpointEvent()
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
		var breakpointedFilePath = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "MyClass.cs");
		debugProtocolHost
			.WithBreakpointsRequest([11], breakpointedFilePath)
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var expectedBreakpointEvent1 = new BreakpointEvent
		{
			Reason = BreakpointEvent.ReasonValue.Changed,
			Breakpoint = new Breakpoint
			{
				Id = 1,
				Message = "Breakpoint has not been processed by the debugger.",
				Verified = false,
				Line = 11
			}
		};
		var breakpointEvent = await debugProtocolHost.WaitForEvent<BreakpointEvent>(debugEventTcs);
		breakpointEvent.Should().BeEquivalentTo(expectedBreakpointEvent1);

		var expectedBreakpointEvent2 = new BreakpointEvent
		{
			Reason = BreakpointEvent.ReasonValue.Changed,
			Breakpoint = new Breakpoint
			{
				Id = 1,
				Verified = true,
				Line = 11,
				EndLine = 11,
				Offset = 0,
				Source = new Source { Path = breakpointedFilePath, Name = "MyClass.cs", SourceReference = 0 }
			}
		};

		var breakpointEvent2 = await debugProtocolHost.WaitForEvent<BreakpointEvent>(debugEventTcs);
		breakpointEvent2.Should().BeEquivalentTo(expectedBreakpointEvent2, options => options.Excluding(s => s.Breakpoint.Source.Checksums).Excluding(s => s.Breakpoint.Source.VsSourceLinkInfo));

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(debugEventTcs);
		var stopInfo = stoppedEvent.ReadStopInfo();
		stopInfo.filePath.Should().EndWith("MyClass.cs");
		stopInfo.line.Should().Be(11);
	}
}
