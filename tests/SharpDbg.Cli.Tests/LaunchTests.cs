using AwesomeAssertions;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using SharpDbg.Cli.Tests.Helpers;

namespace SharpDbg.Cli.Tests;

public class LaunchTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task StopAtEntry_StopsInMainWithEntryReason()
	{
		var (host, initializedEventTcs, debugEventTcs, adapter) = TestHelper.GetLaunchDebugProtocolHostInProc(testOutputHelper);
		using var _ = adapter;
		using var __ = host;
		var program = Path.JoinFromGitRoot("artifacts", "bin", "DebuggableConsoleApp", "debug", "DebuggableConsoleApp.dll");

		await host
			.WithInitializeRequest()
			.WithLaunchRequest(program, stopAtEntry: true, justMyCode: true)
			.WaitForInitializedEvent(initializedEventTcs);
		host.WithConfigurationDoneRequest();

		var stoppedEvent = await host.WaitForStoppedEvent(debugEventTcs);
		stoppedEvent.Reason.Should().Be(StoppedEvent.ReasonValue.Entry);
		stoppedEvent.ThreadId.Should().NotBeNull();
		var topFrame = host.GetTopStackFrame(stoppedEvent.ThreadId!.Value);
		topFrame.Name.Should().Contain("Main");
		topFrame.Source?.Path.Should().EndWith("Program.cs");
	}

	[Fact]
	public async Task StopAtEntry_TopLevelStatementsEntrypointStopsInMainWithEntryReason()
	{
		var (host, initializedEventTcs, debugEventTcs, adapter) = TestHelper.GetLaunchDebugProtocolHostInProc(testOutputHelper);
		using var _ = adapter;
		using var __ = host;
		var program = Path.JoinFromGitRoot("artifacts", "bin", "TopLevelStatementsConsoleApp", "debug", "TopLevelStatementsConsoleApp.dll");

		await host
			.WithInitializeRequest()
			.WithLaunchRequest(program, stopAtEntry: true, justMyCode: true)
			.WaitForInitializedEvent(initializedEventTcs);
		host.WithConfigurationDoneRequest();

		var stoppedEvent = await host.WaitForStoppedEvent(debugEventTcs);
		stoppedEvent.Reason.Should().Be(StoppedEvent.ReasonValue.Entry);
		stoppedEvent.ThreadId.Should().NotBeNull();
		var topFrame = host.GetTopStackFrame(stoppedEvent.ThreadId!.Value);
		topFrame.Name.Should().Contain("Main");
		topFrame.Source?.Path.Should().EndWith("Program.cs");
		topFrame.Line.Should().Be(2);
		host
			.WithScopesRequest(topFrame.Id, out var scopesResponse)
			.WithVariablesRequest(scopesResponse.Scopes.Single().VariablesReference, out var variables);
		variables.Should().Contain(variable => variable.Name == "test");
	}

	[Fact]
	public async Task StopAtEntry_AsyncEntrypointStopsInMainWithEntryReason()
	{
		var (host, initializedEventTcs, debugEventTcs, adapter) = TestHelper.GetLaunchDebugProtocolHostInProc(testOutputHelper);
		using var _ = adapter;
		using var __ = host;
		var program = Path.JoinFromGitRoot("artifacts", "bin", "AsyncEntryConsoleApp", "debug", "AsyncEntryConsoleApp.dll");

		await host
			.WithInitializeRequest()
			.WithLaunchRequest(program, stopAtEntry: true, justMyCode: true)
			.WaitForInitializedEvent(initializedEventTcs);
		host.WithConfigurationDoneRequest();

		var stoppedEvent = await host.WaitForStoppedEvent(debugEventTcs);
		stoppedEvent.Reason.Should().Be(StoppedEvent.ReasonValue.Entry);
		stoppedEvent.ThreadId.Should().NotBeNull();
		var topFrame = host.GetTopStackFrame(stoppedEvent.ThreadId!.Value);
		topFrame.Name.Should().Contain("Main");
		topFrame.Source?.Path.Should().EndWith("Program.cs");
		topFrame.Line.Should().Be(5);
		host
			.WithScopesRequest(topFrame.Id, out var scopesResponse)
			.WithVariablesRequest(scopesResponse.Scopes.Single().VariablesReference, out var variables);
		variables.Should().Contain(variable => variable.Name == "test");
	}
}
