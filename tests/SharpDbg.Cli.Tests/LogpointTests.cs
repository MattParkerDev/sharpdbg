using AwesomeAssertions;
using SharpDbg.Cli.Tests.Helpers;
using SharpDbg.DapProtocol;
using SharpDbg.Infrastructure.Debugger;

namespace SharpDbg.Cli.Tests;

public class LogpointTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task Logpoint_WritesTheInterpolatedMessageAndContinues()
	{
		var sourcePath = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "MyClass.cs");
		var (debugProtocolHost, adapter) = TestHelper.GetLaunchDebugProtocolHostInProc(testOutputHelper);
		using var _ = adapter;
		using var __ = debugProtocolHost;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithLaunchRequest(Path.JoinFromGitRoot("artifacts", "bin", "DebuggableConsoleApp", "debug", "DebuggableConsoleApp.dll"), stopAtEntry: false, justMyCode: true)
			.WaitForInitializedEvent();
		List<SharpDbgBreakpointRequest> breakpoints =
		[
			new SharpDbgBreakpointRequest(21, LogMessage: "myInt={myInt}"),
			new SharpDbgBreakpointRequest(22)
		];
		debugProtocolHost
			.WithBreakpointsRequest(sourcePath, breakpoints)
			.WithConfigurationDoneRequest();

		// The logpoint at line 21 writes its message and continues, so the next stop is the plain breakpoint at 22.
		var output = await debugProtocolHost.WaitForEvent<OutputEvent>(e => e.Output.Contains("myInt=4"));
		output.Output.Should().Contain("myInt=4");

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
		debugProtocolHost.GetTopStackFrame(stoppedEvent.ThreadId!.Value).Line.Should().Be(22);
	}
}
