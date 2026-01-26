using AwesomeAssertions;
using SharpDbg.Cli.Tests.Helpers;

namespace SharpDbg.Cli.Tests;

public class StopAtEntryTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task StopAtEntry_ResumeContinues_AndHitsBreakpoint()
    {
        var startSuspended = true;

        var (debugProtocolHost, initializedEventTcs, stoppedEventTcs, adapter, p2) = TestHelper.GetRunningDebugProtocolHostInProc(testOutputHelper, startSuspended);
        using var _ = adapter;
        using var __ = new ProcessKiller(p2);

        await debugProtocolHost
            .WithInitializeRequest()
            .WithAttachRequest(p2.Id)
            .WaitForInitializedEvent(initializedEventTcs);

        // Compute a sensible breakpoint line inside Program.cs (the "Log2" WriteLine)
        var programPath = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "Program.cs");
        var lines = File.ReadAllLines(programPath);
        var bpLine = Array.FindIndex(lines, l => l.Contains("Log2")) + 1;
        if (bpLine == 0) bpLine = 4; // fallback

        debugProtocolHost.WithBreakpointsRequest(bpLine, programPath)
            .WithConfigurationDoneRequest()
            .WithOptionalResumeRuntime(p2.Id, startSuspended);

        // Now we should hit the breakpoint we just set
        var stoppedEvent2 = await debugProtocolHost.WaitForStoppedEvent(stoppedEventTcs).WaitAsync(TimeSpan.FromSeconds(15));
        var stopInfo2 = stoppedEvent2.ReadStopInfo();
        stopInfo2.filePath.Should().EndWith("Program.cs");
        stopInfo2.line.Should().Be(bpLine);
    }
}
