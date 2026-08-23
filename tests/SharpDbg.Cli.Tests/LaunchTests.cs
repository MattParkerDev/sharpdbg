using AwesomeAssertions;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using SharpDbg.Cli.Tests.Helpers;

namespace SharpDbg.Cli.Tests;

public class LaunchTests(ITestOutputHelper testOutputHelper)
{
	private static string DebuggeeDirectory => Path.JoinFromGitRoot("artifacts", "bin", "DebuggableConsoleApp", "debug");

	[Fact]
	public async Task SharpDbgCli_LaunchManagedAssembly_StopsAtBreakpoint()
	{
		await LaunchAndStopAtBreakpoint(Path.Join(DebuggeeDirectory, "DebuggableConsoleApp.dll"));
	}

	// Handing an apphost to the muxer starts the muxer itself, which the debugger then attaches to instead of the program
	[Fact]
	public async Task SharpDbgCli_LaunchAppHost_StopsAtBreakpoint()
	{
		await LaunchAndStopAtBreakpoint(Path.Join(DebuggeeDirectory, OperatingSystem.IsWindows() ? "DebuggableConsoleApp.exe" : "DebuggableConsoleApp"));
	}

	private async Task LaunchAndStopAtBreakpoint(string program)
	{
		File.Exists(program).Should().BeTrue($"the debuggee must be built: {program}");

		var (debugProtocolHost, initializedEventTcs, firstStoppedEventTcs, adapter) = TestHelper.GetRunningDebugProtocolHostForLaunchInProc(testOutputHelper);
		using var _ = new LaunchedDebuggeeKiller(); // declared first so it runs last, on whatever survives the disconnect
		using var __ = adapter;
		using var ___ = debugProtocolHost;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithLaunchRequest(program)
			.WaitForInitializedEvent(initializedEventTcs);

		var breakpointedFilePath = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "MyClass.cs");
		await debugProtocolHost
			.WithBreakpointsRequest([11], breakpointedFilePath)
			.WithConfigurationDoneRequestAsync(); // the program is started here

		var stoppedEvent = await firstStoppedEventTcs.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
		stoppedEvent.Reason.Should().Be(StoppedEvent.ReasonValue.Breakpoint);
		var (filePath, line, _) = stoppedEvent.ReadStopInfo();
		filePath.Should().Be(breakpointedFilePath);
		line.Should().Be(11);
	}
}
