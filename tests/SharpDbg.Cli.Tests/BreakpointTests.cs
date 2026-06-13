using AwesomeAssertions;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using SharpDbg.Cli.Tests.Helpers;

namespace SharpDbg.Cli.Tests;

public class BreakpointTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task SharpDbgCli_SetBreakpoint_RaisesBreakpointEvent()
	{
		var startSuspended = true;
		var (debugProtocolHost, initializedEventTcs, debugEventTcs, adapter, p2) = TestHelper.GetRunningDebugProtocolHostInProc(testOutputHelper, startSuspended);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(p2.Id)
			.WaitForInitializedEvent(initializedEventTcs);
		var breakpointedFilePath = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "MyClass.cs");
		debugProtocolHost
			.WithBreakpointsRequest([11], breakpointedFilePath)
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var expectedBreakpointEvent1 = new BreakpointEvent
		{
			Reason = BreakpointEvent.ReasonValue.Changed,
			Breakpoint = new Breakpoint
			{
				Id = 1,
				Message = "Breakpoint has not been processed by the debugger.",
				Verified = false,
				Line = 11
			}
		};
		var breakpointEvent = await debugProtocolHost.WaitForEvent<BreakpointEvent>(debugEventTcs);
		breakpointEvent.Should().BeEquivalentTo(expectedBreakpointEvent1);

		var expectedBreakpointEvent2 = new BreakpointEvent
		{
			Reason = BreakpointEvent.ReasonValue.Changed,
			Breakpoint = new Breakpoint
			{
				Id = 1,
				Verified = true,
				Line = 11,
				EndLine = 11,
				Offset = 0,
				Source = new Source { Path = breakpointedFilePath, Name = "MyClass.cs", SourceReference = 0 }
			}
		};

		var breakpointEvent2 = await debugProtocolHost.WaitForEvent<BreakpointEvent>(debugEventTcs);
		breakpointEvent2.Should().BeEquivalentTo(expectedBreakpointEvent2, options => options.Excluding(s => s.Breakpoint.Source.Checksums).Excluding(s => s.Breakpoint.Source.VsSourceLinkInfo));

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(debugEventTcs);
		var stopInfo = stoppedEvent.ReadStopInfo();
		stopInfo.filePath.Should().EndWith("MyClass.cs");
		stopInfo.line.Should().Be(11);
	}

	[Fact]
	public async Task SharpDbgCli_SetBreakpoint_WithColumn_StopsAtMatchingStatementColumn()
	{
		var startSuspended = true;
		var (debugProtocolHost, initializedEventTcs, debugEventTcs, adapter, p2) = TestHelper.GetRunningDebugProtocolHostInProc(testOutputHelper, startSuspended);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(p2.Id)
			.WaitForInitializedEvent(initializedEventTcs);

		var breakpointedFilePath = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "ColumnBreakpointClass.cs");
		var line = GetLineNumber(breakpointedFilePath, "column-breakpoint-line");
		var column = GetColumnNumber(breakpointedFilePath, line, "var third");
		var endColumn = column + "var third = second + 1;".Length;

		SendSetBreakpointsRequest(debugProtocolHost, breakpointedFilePath, new SourceBreakpoint
		{
			Line = line,
			Column = column
		});

		debugProtocolHost
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var breakpointEvent = await WaitForVerifiedBreakpointEvent(debugProtocolHost, debugEventTcs);
		breakpointEvent.Breakpoint.Line.Should().Be(line);
		breakpointEvent.Breakpoint.Column.Should().Be(column);
		breakpointEvent.Breakpoint.EndColumn.Should().Be(endColumn);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(debugEventTcs);
		debugProtocolHost.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTraceResponse);
		var topFrame = stackTraceResponse.StackFrames.Single();

		topFrame.Source.Path.Should().EndWith("ColumnBreakpointClass.cs");
		topFrame.Line.Should().Be(line);
		topFrame.Column.Should().Be(column);
		topFrame.EndColumn.Should().Be(endColumn);
	}

	[Fact]
	public async Task SharpDbgCli_SetBreakpoint_WithColumnOnMultilineStatement_StopsAtStatementStartColumn()
	{
		var startSuspended = true;
		var (debugProtocolHost, initializedEventTcs, debugEventTcs, adapter, p2) = TestHelper.GetRunningDebugProtocolHostInProc(testOutputHelper, startSuspended);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(p2.Id)
			.WaitForInitializedEvent(initializedEventTcs);

		var breakpointedFilePath = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "ColumnBreakpointClass.cs");
		var statementStartLine = GetLineNumber(breakpointedFilePath, "var multi =");
		var requestedLine = statementStartLine + 1;
		var requestedColumn = GetColumnNumber(breakpointedFilePath, requestedLine, "1 + 25");
		var expectedColumn = GetColumnNumber(breakpointedFilePath, statementStartLine, "var multi");

		SendSetBreakpointsRequest(debugProtocolHost, breakpointedFilePath, new SourceBreakpoint
		{
			Line = requestedLine,
			Column = requestedColumn
		});

		debugProtocolHost
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(debugEventTcs);
		debugProtocolHost.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTraceResponse);
		var topFrame = stackTraceResponse.StackFrames.Single();

		topFrame.Source.Path.Should().EndWith("ColumnBreakpointClass.cs");
		topFrame.Line.Should().Be(statementStartLine);
		topFrame.Column.Should().Be(expectedColumn);
	}

	private static void SendSetBreakpointsRequest(DebugProtocolHost debugProtocolHost, string filePath, params SourceBreakpoint[] breakpoints)
	{
		var response = debugProtocolHost.SendRequestSync(new SetBreakpointsRequest
		{
			Source = new Source { Path = filePath },
			Breakpoints = breakpoints.ToList()
		});

		response.Breakpoints.Should().HaveCount(breakpoints.Length);
	}

	private static async Task<BreakpointEvent> WaitForVerifiedBreakpointEvent(DebugProtocolHost debugProtocolHost, TcsContainer debugEventTcs)
	{
		while (true)
		{
			var breakpointEvent = await debugProtocolHost.WaitForEvent<BreakpointEvent>(debugEventTcs);
			if (breakpointEvent.Breakpoint.Verified is true) return breakpointEvent;
		}
	}

	private static int GetLineNumber(string filePath, string marker)
	{
		return File.ReadLines(filePath)
			.Select((line, index) => (line, index))
			.Single(item => item.line.Contains(marker))
			.index + 1;
	}

	private static int GetColumnNumber(string filePath, int lineNumber, string marker)
	{
		var line = File.ReadLines(filePath).ElementAt(lineNumber - 1);
		var index = line.IndexOf(marker, StringComparison.Ordinal);
		if (index < 0) throw new InvalidOperationException($"Marker '{marker}' not found on line {lineNumber} in {filePath}");
		return index + 1;
	}
}
