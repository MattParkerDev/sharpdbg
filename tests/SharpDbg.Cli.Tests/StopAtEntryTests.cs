using AwesomeAssertions;
using SharpDbg.Cli.Tests.Helpers;

namespace SharpDbg.Cli.Tests;

public class StopAtEntryTests(ITestOutputHelper testOutputHelper)
{
	private static (string program, string[] args) GetLaunchArgs()
	{
		var dllPath = Path.JoinFromGitRoot("artifacts", "bin", "DebuggableConsoleApp", "debug", "DebuggableConsoleApp.dll");
		var lookupCommand = OperatingSystem.IsWindows() ? "where" : "which";
		var dotnetPath = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
		{
			FileName = lookupCommand,
			Arguments = "dotnet",
			RedirectStandardOutput = true,
			UseShellExecute = false
		})!.StandardOutput.ReadToEnd().Split('\n')[0].Trim();
		return (dotnetPath, [dllPath]);
	}

	[Fact]
	public async Task SharpDbgCli_LaunchWithStopAtEntry_StopsBeforeFirstUserCode()
	{
		var (dotnetPath, args) = GetLaunchArgs();

		var (debugProtocolHost, initializedEventTcs, stoppedEventTcs, adapter) = TestHelper.GetRunningDebugProtocolHostForLaunch(testOutputHelper);
		using var _ = adapter;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithLaunchRequest(dotnetPath, args, stopAtEntry: true)
			.WaitForInitializedEvent(initializedEventTcs);
		debugProtocolHost.WithConfigurationDoneRequest();

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(stoppedEventTcs);
		var stopInfo = stoppedEvent.ReadStopInfo();

		stopInfo.filePath.Should().NotBeNullOrEmpty();
		stopInfo.line.Should().BeGreaterThan(0);

		debugProtocolHost.WithDisconnectRequest(terminateDebuggee: true);
	}

	[Fact]
	public async Task SharpDbgCli_LaunchWithStopAtEntry_UserBreakpointHitsAfterEntry()
	{
		var (dotnetPath, args) = GetLaunchArgs();

		var (debugProtocolHost, initializedEventTcs, stoppedEventTcs, adapter) = TestHelper.GetRunningDebugProtocolHostForLaunch(testOutputHelper);
		using var _ = adapter;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithLaunchRequest(dotnetPath, args, stopAtEntry: true)
			.WaitForInitializedEvent(initializedEventTcs);

		debugProtocolHost
			.WithBreakpointsRequest(22, Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "MyClass.cs"))
			.WithConfigurationDoneRequest();

		// First stop is the entry stop
		var entryStop = await debugProtocolHost.WaitForStoppedEvent(stoppedEventTcs);
		entryStop.ThreadId.Should().NotBeNull();

		// Continue to hit the user breakpoint
		var userStop = await debugProtocolHost
			.WithContinueRequest()
			.WaitForStoppedEvent(stoppedEventTcs);
		var stopInfo = userStop.ReadStopInfo();

		stopInfo.filePath.Should().EndWith("MyClass.cs");
		stopInfo.line.Should().Be(22);

		debugProtocolHost.WithDisconnectRequest(terminateDebuggee: true);
	}

	[Fact]
	public async Task SharpDbgCli_LaunchWithoutStopAtEntry_HitsUserBreakpointDirectly()
	{
		var (dotnetPath, args) = GetLaunchArgs();

		var (debugProtocolHost, initializedEventTcs, stoppedEventTcs, adapter) = TestHelper.GetRunningDebugProtocolHostForLaunch(testOutputHelper);
		using var _ = adapter;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithLaunchRequest(dotnetPath, args, stopAtEntry: false)
			.WaitForInitializedEvent(initializedEventTcs);

		debugProtocolHost
			.WithBreakpointsRequest(20, Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "MyClass.cs"))
			.WithConfigurationDoneRequest();

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(stoppedEventTcs);
		var stopInfo = stoppedEvent.ReadStopInfo();

		stopInfo.filePath.Should().EndWith("MyClass.cs");
		stopInfo.line.Should().Be(20);

		debugProtocolHost.WithDisconnectRequest(terminateDebuggee: true);
	}
}
