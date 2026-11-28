using AwesomeAssertions;
using SharpDbg.Cli.Tests.Helpers;
using SharpDbg.DapProtocol;

namespace SharpDbg.Cli.Tests;

public class EmbeddedSourceTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task Source_ServesTheTextEmbeddedInThePdb()
	{
		var (debugProtocolHost, adapter) = TestHelper.GetLaunchDebugProtocolHostInProc(testOutputHelper);
		using var _ = adapter;
		using var __ = debugProtocolHost;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithLaunchRequest(Path.JoinFromGitRoot("artifacts", "bin", "EmbeddedSourceApp", "debug", "EmbeddedSourceApp.dll"), stopAtEntry: false, justMyCode: true)
			.WaitForInitializedEvent();
		debugProtocolHost
			.WithBreakpointsRequest([16], Path.JoinFromGitRoot("tests", "EmbeddedSourceApp", "Program.cs"))
			.WithConfigurationDoneRequest();

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
		var frame = debugProtocolHost.GetTopStackFrame(stoppedEvent.ThreadId!.Value);
		frame.Source.Should().NotBeNull();
		frame.Source!.SourceReference.Should().BeGreaterThan(0);

		var response = debugProtocolHost.SendRequestSync(new SourceArguments { SourceReference = frame.Source.SourceReference!.Value });
		response.Content.Should().Contain("embeddedMarker");
	}
}
