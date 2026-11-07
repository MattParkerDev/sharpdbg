using AwesomeAssertions;
using SharpDbg.DapProtocol;
using SharpDbg.Cli.Tests.Helpers;

namespace SharpDbg.Cli.Tests;

// https://github.com/MattParkerDev/sharpdbg/issues/57
// SomeFolder/SameFileName.cs has the same name as SameFileName.cs, and runs first: a breakpoint bound by file name alone stops there
public class BreakpointResolveTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task SetBreakpoints_PathSpellings_BindToTheFileTheyName_AndStopOncePerLocation()
	{
		var startSuspended = true;
		var (debugProtocolHost, adapter, p2) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(p2.Id)
			.WaitForInitializedEvent();

		var filePath = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "SameFileName.cs");
		var otherFilePath = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "SomeFolder", "SameFileName.cs");
		var breakpointIds = new Dictionary<(string Path, int Line), int>();
		// Absolute paths to both files: whichever of the two the PDB lists first, matching by name would bind one of them to the other
		SetBreakpoints(otherFilePath, 16);
		SetBreakpoints(filePath, 9, 15); // and a second breakpoint on line 15
		SetBreakpoints("../SameFileName.cs", 10, 15); // a leading '..' is dropped, leaving the file name
		SetBreakpoints("DebuggableConsoleApp/SameFileName.cs", 11);
		SetBreakpoints("./DebuggableConsoleApp/SomeFolder/../SameFileName.cs", 12);
		SetBreakpoints("../wrong_folder/./SameFileName.cs", 13); // names a folder that holds no such file, so must not bind
		SetBreakpoints("./DebuggableConsoleApp/SAMEFILENAME.CS", 14); // binds only where paths are case-insensitive
		debugProtocolHost
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		// SomeFolder/SameFileName.Test runs first
		List<(string FilePath, int Line, int[] HitBreakpointIds)> expectedStops =
		[
			(otherFilePath, 16, [breakpointIds[(otherFilePath, 16)]]),
			(filePath, 9, [breakpointIds[(filePath, 9)]]),
			(filePath, 10, [breakpointIds[("../SameFileName.cs", 10)]]),
			(filePath, 11, [breakpointIds[("DebuggableConsoleApp/SameFileName.cs", 11)]]),
			(filePath, 12, [breakpointIds[("./DebuggableConsoleApp/SomeFolder/../SameFileName.cs", 12)]]),
			.. OperatingSystem.IsLinux() ? [] : new List<(string, int, int[])> { (filePath, 14, [breakpointIds[("./DebuggableConsoleApp/SAMEFILENAME.CS", 14)]]) },
			(filePath, 15, [breakpointIds[(filePath, 15)], breakpointIds[("../SameFileName.cs", 15)]]),
			// The next pass of the debuggee's loop: line 15 stopped only once
			(otherFilePath, 16, [breakpointIds[(otherFilePath, 16)]]),
		];
		foreach (var (index, (expectedFilePath, expectedLine, expectedHitBreakpointIds)) in expectedStops.Index())
		{
			if (index > 0) debugProtocolHost.WithContinueRequest();
			var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
			var topFrame = debugProtocolHost.GetTopStackFrame(stoppedEvent.ThreadId!.Value);
			topFrame.Source!.Path.Should().Be(expectedFilePath);
			topFrame.Line.Should().Be(expectedLine);
			stoppedEvent.HitBreakpointIds.Should().BeEquivalentTo(expectedHitBreakpointIds);
		}

		void SetBreakpoints(string path, params int[] lines)
		{
			var response = debugProtocolHost.SendRequestSync(new SetBreakpointsArguments
			{
				Source = new Source { Path = path },
				Breakpoints = lines.Select(line => new SourceBreakpoint { Line = line }).ToList()
			});
			foreach (var (line, breakpoint) in lines.Zip(response.Breakpoints))
			{
				breakpointIds[(path, line)] = breakpoint.Id!.Value;
			}
		}
	}
}
