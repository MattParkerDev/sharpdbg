using AwesomeAssertions;
using SharpDbg.Cli.Tests.Helpers;

namespace SharpDbg.Cli.Tests;

public class IndexerTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task VariablesRequest_ObjectWithIndexer_DoesNotListTheIndexer()
	{
		var startSuspended = true;
		var (debugProtocolHost, initializedEventTcs, debugEventTcs, adapter, p2) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(p2.Id)
			.WaitForInitializedEvent(initializedEventTcs);
		debugProtocolHost
			.WithBreakpointsRequest(11, Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "ClassWithIndexer.cs"))
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(debugEventTcs);
		var topFrame = debugProtocolHost.GetTopStackFrame(stoppedEvent.ThreadId!.Value);
		debugProtocolHost
			.WithScopesRequest(topFrame.Id, out var scopesResponse)
			.WithVariablesRequest(scopesResponse.Scopes.Single().VariablesReference, out var variables);

		debugProtocolHost.WithVariablesRequest(variables.Single(v => v.Name == "classWithIndexer").VariablesReference, out var members);
		members.Select(v => v.Name).Should().Equal("Value");
	}
}
