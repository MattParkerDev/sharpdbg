using AwesomeAssertions;
using SharpDbg.Cli.Tests.Helpers;
using SharpDbg.DapProtocol;

namespace SharpDbg.Cli.Tests;

public class SetVariableTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task SetVariable_UpdatesStackVariables()
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

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
		debugProtocolHost
			.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTraceResponse)
			.WithScopesRequest(stackTraceResponse.StackFrames!.First().Id, out var scopesResponse);
		var localsReference = scopesResponse.Scopes.Single().VariablesReference;

		var response = debugProtocolHost.SendRequestSync(new SetVariableArguments
		{
			VariablesReference = localsReference,
			Name = "myInt",
			Value = "42"
		});
		response.Value.Should().Be("42");

		// The write has to survive a fresh read, not only be echoed in the response.
		debugProtocolHost.WithVariablesRequest(localsReference, out var variables);
		variables.Single(v => v.Name == "myInt").Value.Should().Be("42");
	}
}
