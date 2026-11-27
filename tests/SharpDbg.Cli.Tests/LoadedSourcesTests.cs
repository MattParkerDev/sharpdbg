using AwesomeAssertions;
using SharpDbg.Cli.Tests.Helpers;
using SharpDbg.DapProtocol;

namespace SharpDbg.Cli.Tests;

public class LoadedSourcesTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task LoadedSources_ReturnsSourceFilesFromTheStoppedModule()
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

		var response = debugProtocolHost.SendRequestSync(new LoadedSourcesArguments());

		response.Sources.Should().NotBeEmpty();
		response.Sources.Should().Contain(source => source.Path!.EndsWith("MyClass.cs", StringComparison.OrdinalIgnoreCase));
	}
}
