using AwesomeAssertions;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using SharpDbg.Cli.Tests.Helpers;

namespace SharpDbg.Cli.Tests;

public class OutputTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task SharpDbgCli_Launch_ForwardsDebuggeeStdoutAndStderrAsOutputEvents()
	{
		var stdoutEventTcs = new TaskCompletionSource<OutputEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
		var stderrEventTcs = new TaskCompletionSource<OutputEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
		var (debugProtocolHost, initializedEventTcs, adapter) = TestHelper.GetRunningDebugProtocolHostForLaunchInProc(testOutputHelper, outputEvent =>
		{
			if (outputEvent.Output.Contains("DebuggableConsoleApp is running")) stdoutEventTcs.TrySetResult(outputEvent);
			if (outputEvent.Output.Contains("DebuggableConsoleApp stderr")) stderrEventTcs.TrySetResult(outputEvent);
		});
		using var _ = adapter;
		using var __ = debugProtocolHost;

		var program = Path.JoinFromGitRoot("artifacts", "bin", "DebuggableConsoleApp", "debug", "DebuggableConsoleApp.dll");
		await debugProtocolHost
			.WithInitializeRequest()
			.WithLaunchRequest(program, "--print-stderr-and-exit")
			.WaitForInitializedEvent(initializedEventTcs);
		debugProtocolHost.WithConfigurationDoneRequest();

		var stdoutEvent = await stdoutEventTcs.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
		stdoutEvent.Category.Should().Be(OutputEvent.CategoryValue.Stdout);
		var stderrEvent = await stderrEventTcs.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
		stderrEvent.Category.Should().Be(OutputEvent.CategoryValue.Stderr);
	}
}
