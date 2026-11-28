using AwesomeAssertions;
using SharpDbg.Cli.Tests.Helpers;
using SharpDbg.DapProtocol;

namespace SharpDbg.Cli.Tests;

public class BreakpointLocationsTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task BreakpointLocations_ReturnsTheLineThatCanBeBrokenOn()
	{
		var startSuspended = true;
		var (debugProtocolHost, adapter, process) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended);
		using var _ = adapter;
		using var __ = new ProcessKiller(process);
		using var ___ = debugProtocolHost;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(process.Id)
			.WaitForInitializedEvent();
		debugProtocolHost
			.WithBreakpointsRequest()
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(process.Id, startSuspended);
		await debugProtocolHost.WaitForStoppedEvent();

		var response = debugProtocolHost.SendRequestSync(new BreakpointLocationsArguments
		{
			Source = new Source { Path = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "MyClass.cs") },
			Line = 22
		});

		response.Breakpoints.Should().NotBeEmpty();
		response.Breakpoints.Should().Contain(location => location.Line == 22);
	}
}
