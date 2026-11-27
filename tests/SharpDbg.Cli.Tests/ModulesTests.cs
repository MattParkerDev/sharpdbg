using AwesomeAssertions;
using SharpDbg.Cli.Tests.Helpers;
using SharpDbg.DapProtocol;

namespace SharpDbg.Cli.Tests;

public class ModulesTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task Modules_ReturnsTheDebuggeeModule()
	{
		var (debugProtocolHost, adapter) = TestHelper.GetLaunchDebugProtocolHostInProc(testOutputHelper);
		using var _ = adapter;
		using var __ = debugProtocolHost;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithLaunchRequest(Path.JoinFromGitRoot("artifacts", "bin", "DebuggableConsoleApp", "debug", "DebuggableConsoleApp.dll"), stopAtEntry: true, justMyCode: true)
			.WaitForInitializedEvent();
		debugProtocolHost.WithConfigurationDoneRequest();
		await debugProtocolHost.WaitForStoppedEvent();

		var response = debugProtocolHost.SendRequestSync(new ModulesArguments());

		response.TotalModules.Should().Be(response.Modules.Count);
		var debuggee = response.Modules.Should().ContainSingle(module => module.Name == "DebuggableConsoleApp.dll").Subject;
		debuggee.IsUserCode.Should().BeTrue();
		debuggee.SymbolStatus.Should().Be("Symbols Loaded");
	}
}
