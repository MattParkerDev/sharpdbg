using AwesomeAssertions;
using SharpDbg.Application.Protocol;
using SharpDbg.Cli.Tests.Helpers;
using SharpDbg.DapProtocol;

namespace SharpDbg.Cli.Tests;

public class SourceLinkTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task SourceLink_MapsSourceToRepositoryUrl()
	{
		var sourcePath = Path.JoinFromGitRoot("tests", "EmbeddedSourceApp", "Program.cs");
		var (debugProtocolHost, adapter) = TestHelper.GetLaunchDebugProtocolHostInProc(testOutputHelper);
		using var _ = adapter;
		using var __ = debugProtocolHost;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithLaunchRequest(Path.JoinFromGitRoot("artifacts", "bin", "EmbeddedSourceApp", "debug", "EmbeddedSourceApp.dll"), stopAtEntry: false, justMyCode: true)
			.WaitForInitializedEvent();
		debugProtocolHost
			.WithBreakpointsRequest([16], sourcePath)
			.WithConfigurationDoneRequest();
		await debugProtocolHost.WaitForStoppedEvent();

		var response = debugProtocolHost.SendRequestSync(new SourceLinkArguments { Path = sourcePath });

		response.Url.Should().NotBeNullOrEmpty("the debuggee PDB carries a SourceLink document");
		response.Url.Should().Contain("sharpdbg");
		response.Url.Should().EndWith("Program.cs");
	}
}
