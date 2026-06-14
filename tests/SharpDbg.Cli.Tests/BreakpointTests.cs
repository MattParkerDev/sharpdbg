using AwesomeAssertions;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using SharpDbg.Cli.Tests.Helpers;
using SharpDbg.Infrastructure.Debugger;

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
				Column = 3,
				EndLine = 11,
				EndColumn = 17,
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

		debugProtocolHost
			.WithBreakpointsRequest(breakpointedFilePath, [new SharpDbgBreakpointRequest(line, Column: column)])
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var breakpointEvent = await debugProtocolHost.WaitForEvent<BreakpointEvent>(debugEventTcs, s => s.Breakpoint.Verified);
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
	public async Task SharpDbgCli_SetBreakpoint_WithoutColumnOnMultiStatementLine_StopsAtFirstStatementColumn()
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
		var expectedColumn = GetColumnNumber(breakpointedFilePath, line, "var first");

		debugProtocolHost
			.WithBreakpointsRequest(breakpointedFilePath, [new SharpDbgBreakpointRequest(line)])
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(debugEventTcs);
		debugProtocolHost.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTraceResponse);
		var topFrame = stackTraceResponse.StackFrames.Single();

		topFrame.Source.Path.Should().EndWith("ColumnBreakpointClass.cs");
		topFrame.Line.Should().Be(line);
		topFrame.Column.Should().Be(expectedColumn);
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

		debugProtocolHost
			.WithBreakpointsRequest(breakpointedFilePath, [new SharpDbgBreakpointRequest(requestedLine, Column: requestedColumn)])
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var breakpointEvent = await debugProtocolHost.WaitForEvent<BreakpointEvent>(debugEventTcs, s => s.Breakpoint.Verified);
		breakpointEvent.Breakpoint.Line.Should().Be(statementStartLine);
		breakpointEvent.Breakpoint.Column.Should().Be(expectedColumn);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(debugEventTcs);
		debugProtocolHost.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTraceResponse);
		var topFrame = stackTraceResponse.StackFrames.Single();

		topFrame.Source.Path.Should().EndWith("ColumnBreakpointClass.cs");
		topFrame.Line.Should().Be(statementStartLine);
		topFrame.Column.Should().Be(expectedColumn);
	}

	[Fact]
	public async Task SharpDbgCli_SetBreakpoint_AfterSymbolsLoaded_ResponseIncludesResolvedColumns()
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

		debugProtocolHost
			.WithBreakpointsRequest(breakpointedFilePath, [new SharpDbgBreakpointRequest(line)])
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		await debugProtocolHost.WaitForStoppedEvent(debugEventTcs);

		var column = GetColumnNumber(breakpointedFilePath, line, "var second");
		var endColumn = column + "var second = first + 1;".Length;

		debugProtocolHost.WithBreakpointsRequest(breakpointedFilePath, [new SharpDbgBreakpointRequest(line, Column: column)], out var response);

		var breakpoint = response.Breakpoints.Single();

		breakpoint.Verified.Should().BeTrue();
		breakpoint.Line.Should().Be(line);
		breakpoint.Column.Should().Be(column);
		breakpoint.EndLine.Should().Be(line);
		breakpoint.EndColumn.Should().Be(endColumn);
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
