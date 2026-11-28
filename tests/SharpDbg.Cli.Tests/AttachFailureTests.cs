using AwesomeAssertions;
using SharpDbg.DapProtocol;

namespace SharpDbg.Cli.Tests;

public class AttachFailureTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task ConfigurationDone_WhenAttachRegistrationFails_EndsTheSession()
	{
		var (debugProtocolHost, adapter) = TestHelper.GetLaunchDebugProtocolHostInProc(testOutputHelper);
		using var _ = adapter;
		using var __ = debugProtocolHost;

		// A negative process id fails the runtime startup registration. The client must not be left with a session that never ends
		debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(-1)
			.WithConfigurationDoneRequest();

		var outputEvent = await debugProtocolHost.WaitForEvent<OutputEvent>(o => o.Output.Contains("Attach to process -1 failed"), TimeSpan.FromSeconds(5));
		outputEvent.Category.Should().Be(OutputEvent.CategoryValue.Stderr);
		await debugProtocolHost.WaitForEvent<TerminatedEvent>(timeout: TimeSpan.FromSeconds(5));
	}
}
