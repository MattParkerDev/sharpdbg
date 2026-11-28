using AwesomeAssertions;
using SharpDbg.Cli.Tests.Helpers;
using SharpDbg.DapProtocol;

namespace SharpDbg.Cli.Tests;

public class GotoTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task Goto_MovesTheInstructionPointerAndReportsAGotoStop()
	{
		var sourcePath = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "MyClass.cs");
		var (debugProtocolHost, adapter) = TestHelper.GetLaunchDebugProtocolHostInProc(testOutputHelper);
		using var _ = adapter;
		using var __ = debugProtocolHost;

		var capabilities = debugProtocolHost.SendRequestSync(DebugAdapterProcessHelper.GetInitializeRequest());
		capabilities.SupportsGotoTargetsRequest.Should().BeTrue();

		await debugProtocolHost
			.WithLaunchRequest(Path.JoinFromGitRoot("artifacts", "bin", "DebuggableConsoleApp", "debug", "DebuggableConsoleApp.dll"), stopAtEntry: false, justMyCode: true)
			.WaitForInitializedEvent();
		debugProtocolHost
			.WithBreakpointsRequest([15], sourcePath)
			.WithConfigurationDoneRequest();

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
		debugProtocolHost.GetTopStackFrame(stoppedEvent.ThreadId!.Value).Line.Should().Be(15);

		var targets = debugProtocolHost.SendRequestSync(new GotoTargetsArguments
		{
			Source = new Source { Path = sourcePath },
			Line = 21
		});
		var target = targets.Targets.Should().ContainSingle().Subject;
		target.Line.Should().Be(21);

		debugProtocolHost.SendRequestSync(new GotoArguments
		{
			ThreadId = stoppedEvent.ThreadId.Value,
			TargetId = target.Id
		});

		var gotoStop = await debugProtocolHost.WaitForStoppedEvent();
		gotoStop.Reason.Should().Be(StoppedEvent.ReasonValue.Goto);
		debugProtocolHost.GetTopStackFrame(gotoStop.ThreadId!.Value).Line.Should().Be(21);
	}
}
