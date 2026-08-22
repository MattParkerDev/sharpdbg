using AwesomeAssertions;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using SharpDbg.Cli.Tests.Helpers;

namespace SharpDbg.Cli.Tests;

public class ExceptionTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task SharpDbgCli_Exception_VariablesHasExceptionScope()
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
		debugProtocolHost.SendRequestSync(new SetExceptionBreakpointsRequest { Filters = [], FilterOptions = [new("all"), new("user-unhandled")] });
		var breakpointedFilePath = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "Exceptions.cs");
		debugProtocolHost
			.WithBreakpointsRequest([24], Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "Program.cs"))
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(debugEventTcs);
		var stopInfo = stoppedEvent.ReadStopInfo();
		stopInfo.filePath.Should().EndWith("Program.cs");
		stopInfo.line.Should().Be(24);

		// set 'ExceptionToThrow' to .Normal - we do not want other tests to stop at the 'exception' stop event, only this one
		debugProtocolHost.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTraceResponse);
		debugProtocolHost.WithEvaluateRequest(stackTraceResponse.StackFrames.First().Id, "exceptionToThrow = ExceptionToThrow.Normal", out var evaluateResponse);
		evaluateResponse.Result.Should().Be("Normal");

		debugProtocolHost.WithContinueRequest();

		var stoppedEvent2 = await debugProtocolHost.WaitForStoppedEvent(debugEventTcs);
		var stopInfo2 = stoppedEvent2.ReadStopInfo();
		stopInfo2.filePath.Should().EndWith("Exceptions.cs");
		stopInfo2.line.Should().Be(14); // Where the exception is thrown

		debugProtocolHost
			.WithStackTraceRequest(stoppedEvent2.ThreadId!.Value, out var stackTraceResponse2)
			.WithScopesRequest(stackTraceResponse2.StackFrames!.First().Id, out var scopesResponse2);

		scopesResponse2.Scopes.Should().HaveCount(1);
		var scope = scopesResponse2.Scopes.Single();

		List<Variable> expectedVariables =
		[
			new() { Name = "$exception",  EvaluateName = "$exception",  Value = $"System.InvalidOperationException: Test exception{Environment.NewLine}   at DebuggableConsoleApp.Exceptions.Test(ExceptionToThrow exceptionToThrow) in {breakpointedFilePath}:line 14", Type = "System.InvalidOperationException", VariablesReference = 2 },
			new() { Name = "exceptionToThrow", EvaluateName = "exceptionToThrow", Value = "Normal",  Type = "DebuggableConsoleApp.ExceptionToThrow", VariablesReference = 3 },
		];
		debugProtocolHost.WithVariablesRequest(scope.VariablesReference, out var variables);

		variables.Should().HaveCount(expectedVariables.Count);
		variables.Should().BeEquivalentTo(expectedVariables, options => options.Excluding(s => s.MemoryReference).Excluding(s => s.PresentationHint));

		debugProtocolHost.WithEvaluateRequest(stackTraceResponse.StackFrames.First().Id, "$exception", out var evaluateResponse2);
		evaluateResponse2.Result.Should().Be(expectedVariables[0].Value);

		var expectedExceptionInfoResponse = new ExceptionInfoResponse
		{
			ExceptionId = "CLR/System.InvalidOperationException",
			Description = "Exception thrown: 'System.InvalidOperationException' in DebuggableConsoleApp.dll: 'Test exception'",
			BreakMode = ExceptionBreakMode.Always,
			Code = 0,
			Details = new ExceptionDetails
			{
				Message = "Test exception",
				TypeName = "InvalidOperationException",
				FullTypeName = "System.InvalidOperationException",
				EvaluateName = "$exception",
				StackTrace = $"   at DebuggableConsoleApp.Exceptions.Test(ExceptionToThrow exceptionToThrow) in {breakpointedFilePath}:line 14",
				InnerException = [],
				FormattedDescription = "**System.InvalidOperationException:** 'Test exception'",
				HResult = -2146233079,
				Source = "DebuggableConsoleApp"
			}
		};

		var exceptionInfoResponse = debugProtocolHost.SendRequestSync(new ExceptionInfoRequest(stoppedEvent2.ThreadId.Value));
		exceptionInfoResponse.Should().BeEquivalentTo(expectedExceptionInfoResponse);
	}

	[Fact]
	public async Task ExceptionInExternalCode_JustMyCodeEnabled_HasNoSourceInfo()
	{
		var startSuspended = true;
		var (debugProtocolHost, initializedEventTcs, debugEventTcs, adapter, p2) = TestHelper.GetRunningDebugProtocolHostInProc(testOutputHelper, startSuspended);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(p2.Id, true)
			.WaitForInitializedEvent(initializedEventTcs);
		debugProtocolHost.SendRequestSync(new SetExceptionBreakpointsRequest { Filters = [], FilterOptions = [new("all"), new("user-unhandled")] });
		var breakpointedFilePath = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "Exceptions.cs");
		debugProtocolHost
			.WithBreakpointsRequest([24], Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "Program.cs"))
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(debugEventTcs);
		var stopInfo = stoppedEvent.ReadStopInfo();
		stopInfo.filePath.Should().EndWith("Program.cs");
		stopInfo.line.Should().Be(24);

		// set 'ExceptionToThrow' to .ExternalCode - we do not want other tests to stop at the 'exception' stop event, only this one
		debugProtocolHost.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTraceResponse);
		debugProtocolHost.WithEvaluateRequest(stackTraceResponse.StackFrames.First().Id, "exceptionToThrow = ExceptionToThrow.ExternalCode", out var evaluateResponse);
		evaluateResponse.Result.Should().Be("ExternalCode");

		debugProtocolHost.WithContinueRequest();

		var stoppedEvent2 = await debugProtocolHost.WaitForStoppedEvent(debugEventTcs);
		stoppedEvent2.AdditionalProperties.Should().BeEmpty();

		debugProtocolHost
			.WithStackTraceRequest(stoppedEvent2.ThreadId!.Value, out var stackTraceResponse2, null)
			.WithScopesRequest(stackTraceResponse2.StackFrames!.First().Id, out var scopesResponse2);

		List<StackFrame> expectedStackFrames =
		[
			new() { Id = 1, Column = 0, EndColumn =  0, Line =  0, EndLine =  0, Name = "System.Private.CoreLib.dll!System.Number.ThrowFormatException<char>(System.ReadOnlySpan<char> value)", Source = null },
			new() { Id = 2, Column = 0, EndColumn =  0, Line =  0, EndLine =  0, Name = "System.Private.CoreLib.dll!System.Int32.Parse(string s)",                 Source = null },
			new() { Id = 3, Column = 5, EndColumn = 32, Line = 18, EndLine = 18, Name = "DebuggableConsoleApp.dll!DebuggableConsoleApp.Exceptions.Test(DebuggableConsoleApp.ExceptionToThrow exceptionToThrow)", Source = new Source { Name = "Exceptions.cs", SourceReference = 0, Path = breakpointedFilePath } },
			new() { Id = 4, Column = 4, EndColumn = 38, Line = 34, EndLine = 34, Name = "DebuggableConsoleApp.dll!DebuggableConsoleApp.Program.Main(string[] args)",    Source = new Source { Name = "Program.cs",    SourceReference = 0, Path = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "Program.cs") } },
		];

		stackTraceResponse2.StackFrames.Should().BeEquivalentTo(expectedStackFrames);
		scopesResponse2.Scopes.Should().HaveCount(1);
		var scope = scopesResponse2.Scopes.Single();

		var expectedStackTrace = $"   at System.Number.ThrowFormatException[TChar](ReadOnlySpan`1 value){Environment.NewLine}   at System.Int32.Parse(String s){Environment.NewLine}   at DebuggableConsoleApp.Exceptions.Test(ExceptionToThrow exceptionToThrow) in {breakpointedFilePath}:line 18";
		List<Variable> expectedVariables =
		[
			new() { Name = "$exception",  EvaluateName = "$exception",  Value = $"System.FormatException: The input string 'x' was not in a correct format.{Environment.NewLine}{expectedStackTrace}", Type = "System.FormatException", VariablesReference = 2 }
		];
		debugProtocolHost.WithVariablesRequest(scope.VariablesReference, out var variables);

		variables.Should().HaveCount(expectedVariables.Count);
		variables.Should().BeEquivalentTo(expectedVariables, options => options.Excluding(s => s.MemoryReference).Excluding(s => s.PresentationHint));

		debugProtocolHost.WithEvaluateRequest(stackTraceResponse.StackFrames.First().Id, "$exception", out var evaluateResponse2);
		evaluateResponse2.Result.Should().Be(expectedVariables[0].Value);

		var expectedExceptionInfoResponse = new ExceptionInfoResponse
		{
			ExceptionId = "CLR/System.FormatException",
			Description = "Exception thrown: 'System.FormatException' in System.Private.CoreLib.dll: 'The input string 'x' was not in a correct format.'",
			BreakMode = ExceptionBreakMode.Always,
			Code = 0,
			Details = new ExceptionDetails
			{
				Message = "The input string 'x' was not in a correct format.",
				TypeName = "FormatException",
				FullTypeName = "System.FormatException",
				EvaluateName = "$exception",
				StackTrace = expectedStackTrace,
				InnerException = [],
				FormattedDescription = "**System.FormatException:** 'The input string 'x' was not in a correct format.'",
				HResult = -2146233033,
				Source = "System.Private.CoreLib"
			}
		};

		var exceptionInfoResponse = debugProtocolHost.SendRequestSync(new ExceptionInfoRequest(stoppedEvent2.ThreadId.Value));
		exceptionInfoResponse.Should().BeEquivalentTo(expectedExceptionInfoResponse);

		// Now we should land on the catch block
		var stoppedEvent3 = await debugProtocolHost.WithStepOverRequest(stoppedEvent2.ThreadId!.Value).WaitForStoppedEvent(debugEventTcs);
		var stopInfo3 = stoppedEvent3.ReadStopInfo();
		stopInfo3.Should().Be((breakpointedFilePath, 32, 3));
	}

	[Fact]
	public Task ExceptionFilters_BreakOnAllExceptions()
	{
		return AssertBreaksOnException(
			new SetExceptionBreakpointsRequest { Filters = ["all"], FilterOptions = [] },
			exceptionToThrow: "Normal",
			justMyCode: true,
			expectedExceptionType: "System.InvalidOperationException",
			expectedBreakMode: ExceptionBreakMode.Always);
	}

	[Fact]
	public Task ExceptionFilters_AllExceptionsWithJmcEnabled_DoesNotBreakWhenHandledInExternalCode()
	{
		return AssertContinuesWithoutExceptionStop(
			new SetExceptionBreakpointsRequest { Filters = ["all"], FilterOptions = [] },
			exceptionToThrow: "HandledWithinExternalCode",
			justMyCode: true);
	}

	[Fact]
	public Task ExceptionFilters_AllExceptionsWithJmcDisabled_BreaksWhenHandledInExternalCode()
	{
		return AssertBreaksOnException(
			new SetExceptionBreakpointsRequest { Filters = ["all"], FilterOptions = [] },
			exceptionToThrow: "HandledWithinExternalCode",
			justMyCode: false,
			expectedExceptionType: "System.ArgumentException",
			expectedBreakMode: ExceptionBreakMode.Always);
	}

	[Fact]
	public Task ExceptionFilters_AllExceptionsWithJmcEnabled_BreaksWhenReturnedToUserCode()
	{
		return AssertBreaksOnException(
			new SetExceptionBreakpointsRequest { Filters = ["all"], FilterOptions = [] },
			exceptionToThrow: "ExternalCode",
			justMyCode: true,
			expectedExceptionType: "System.FormatException",
			expectedBreakMode: ExceptionBreakMode.Always);
	}

	[Fact]
	public Task ExceptionFilters_BreakOnUserUnhandledExceptions()
	{
		return AssertBreaksOnException(
			new SetExceptionBreakpointsRequest { Filters = ["user-unhandled"], FilterOptions = [] },
			exceptionToThrow: "UserUnhandled",
			justMyCode: true,
			expectedExceptionType: "System.InvalidOperationException",
			expectedBreakMode: ExceptionBreakMode.UserUnhandled);
	}

	[Fact]
	public Task ExceptionFilters_UserUnhandledDoesNotBreakOnExceptionHandledInUserCode()
	{
		return AssertContinuesWithoutExceptionStop(
			new SetExceptionBreakpointsRequest { Filters = ["user-unhandled"], FilterOptions = [] },
			exceptionToThrow: "Normal",
			justMyCode: true);
	}

	[Theory]
	[InlineData("System.InvalidOperationException", true)]
	[InlineData("System.FormatException", false)]
	public Task ExceptionFilters_UserUnhandledConditionFiltersExceptionTypes(string condition, bool expectExceptionStop)
	{
		var request = new SetExceptionBreakpointsRequest
		{
			Filters = [],
			FilterOptions = [new ExceptionFilterOptions("user-unhandled") { Condition = condition }]
		};

		return expectExceptionStop
			? AssertBreaksOnException(request, exceptionToThrow: "UserUnhandled", justMyCode: true, expectedExceptionType: "System.InvalidOperationException", expectedBreakMode: ExceptionBreakMode.UserUnhandled)
			: AssertContinuesWithoutExceptionStop(request, exceptionToThrow: "UserUnhandled", justMyCode: true);
	}

	[Fact]
	public Task ExceptionFilters_NoFiltersDoesNotBreakOnHandledException()
	{
		return AssertContinuesWithoutExceptionStop(
			new SetExceptionBreakpointsRequest { Filters = [], FilterOptions = [] },
			exceptionToThrow: "Normal",
			justMyCode: true);
	}

	[Theory]
	[InlineData("System.InvalidOperationException", true)]
	[InlineData("System.FormatException", false)]
	public Task ExceptionFilters_AllExceptionsIncludeConditionFiltersExceptionTypes(string condition, bool expectExceptionStop)
	{
		return AssertAllExceptionsCondition(condition, expectExceptionStop);
	}

	[Theory]
	[InlineData("!System.FormatException", true)]
	[InlineData("!System.InvalidOperationException", false)]
	public Task ExceptionFilters_AllExceptionsExcludeConditionFiltersExceptionTypes(string condition, bool expectExceptionStop)
	{
		return AssertAllExceptionsCondition(condition, expectExceptionStop);
	}

	[Theory]
	[InlineData("System.FormatException,System.InvalidOperationException", true)]
	[InlineData("!System.FormatException,System.ArgumentException", true)]
	[InlineData("System.FormatException, System.InvalidOperationException", true)]
	public Task ExceptionFilters_AllExceptionsConditionSupportsMultipleTypes(string condition, bool expectExceptionStop)
	{
		return AssertAllExceptionsCondition(condition, expectExceptionStop);
	}

	private Task AssertAllExceptionsCondition(string condition, bool expectExceptionStop)
	{
		var request = new SetExceptionBreakpointsRequest
		{
			Filters = [],
			FilterOptions = [new ExceptionFilterOptions("all") { Condition = condition }]
		};

		return expectExceptionStop
			? AssertBreaksOnException(request, exceptionToThrow: "Normal", justMyCode: true, expectedExceptionType: "System.InvalidOperationException", expectedBreakMode: ExceptionBreakMode.Always)
			: AssertContinuesWithoutExceptionStop(request, exceptionToThrow: "Normal", justMyCode: true);
	}

	private async Task AssertBreaksOnException(
		SetExceptionBreakpointsRequest exceptionBreakpointsRequest,
		string exceptionToThrow,
		bool justMyCode,
		string expectedExceptionType,
		ExceptionBreakMode expectedBreakMode)
	{
		const bool startSuspended = true;
		var (debugProtocolHost, initializedEventTcs, debugEventTcs, adapter, process) = TestHelper.GetRunningDebugProtocolHostInProc(testOutputHelper, startSuspended);
		using var _ = adapter;
		using var __ = new ProcessKiller(process);
		using var ___ = debugProtocolHost;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(process.Id, justMyCode)
			.WaitForInitializedEvent(initializedEventTcs);
		debugProtocolHost.SendRequestSync(exceptionBreakpointsRequest);

		const int setupBreakpointLine = 24;
		const int completionMarkerLine = 35;
		var programPath = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "Program.cs");
		debugProtocolHost
			.WithBreakpointsRequest([setupBreakpointLine], programPath)
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(process.Id, startSuspended);

		var setupStop = await debugProtocolHost.WaitForStoppedEvent(debugEventTcs);
		setupStop.Reason.Should().Be(StoppedEvent.ReasonValue.Breakpoint);
		var setupStopInfo = setupStop.ReadStopInfo();
		setupStopInfo.filePath.Should().Be(programPath);
		setupStopInfo.line.Should().Be(setupBreakpointLine);

		debugProtocolHost.WithStackTraceRequest(setupStop.ThreadId!.Value, out var stackTraceResponse);
		debugProtocolHost.WithEvaluateRequest(stackTraceResponse.StackFrames.First().Id, $"exceptionToThrow = ExceptionToThrow.{exceptionToThrow}", out var evaluateResponse);
		evaluateResponse.Result.Should().Be(exceptionToThrow);

		debugProtocolHost
			.WithBreakpointsRequest([completionMarkerLine], programPath)
			.WithContinueRequest();

		var exceptionStop = await debugProtocolHost.WaitForStoppedEvent(debugEventTcs);
		exceptionStop.Reason.Should().Be(StoppedEvent.ReasonValue.Exception);

		var exceptionInfo = debugProtocolHost.SendRequestSync(new ExceptionInfoRequest(exceptionStop.ThreadId!.Value));
		exceptionInfo.Details!.FullTypeName.Should().Be(expectedExceptionType);
		exceptionInfo.BreakMode.Should().Be(expectedBreakMode);
	}

	private async Task AssertContinuesWithoutExceptionStop(
		SetExceptionBreakpointsRequest exceptionBreakpointsRequest,
		string exceptionToThrow,
		bool justMyCode)
	{
		const bool startSuspended = true;
		var (debugProtocolHost, initializedEventTcs, debugEventTcs, adapter, process) = TestHelper.GetRunningDebugProtocolHostInProc(testOutputHelper, startSuspended);
		using var _ = adapter;
		using var __ = new ProcessKiller(process);
		using var ___ = debugProtocolHost;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(process.Id, justMyCode)
			.WaitForInitializedEvent(initializedEventTcs);
		debugProtocolHost.SendRequestSync(exceptionBreakpointsRequest);

		const int setupBreakpointLine = 24;
		const int completionMarkerLine = 35;
		var programPath = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "Program.cs");
		debugProtocolHost
			.WithBreakpointsRequest([setupBreakpointLine], programPath)
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(process.Id, startSuspended);

		var setupStop = await debugProtocolHost.WaitForStoppedEvent(debugEventTcs);
		setupStop.Reason.Should().Be(StoppedEvent.ReasonValue.Breakpoint);
		var setupStopInfo = setupStop.ReadStopInfo();
		setupStopInfo.filePath.Should().Be(programPath);
		setupStopInfo.line.Should().Be(setupBreakpointLine);

		debugProtocolHost.WithStackTraceRequest(setupStop.ThreadId!.Value, out var stackTraceResponse);
		debugProtocolHost.WithEvaluateRequest(stackTraceResponse.StackFrames.First().Id, $"exceptionToThrow = ExceptionToThrow.{exceptionToThrow}", out var evaluateResponse);
		evaluateResponse.Result.Should().Be(exceptionToThrow);

		debugProtocolHost
			.WithBreakpointsRequest([completionMarkerLine], programPath)
			.WithContinueRequest();

		var completionStop = await debugProtocolHost.WaitForStoppedEvent(debugEventTcs);
		var completionStackFrame = debugProtocolHost.GetTopStackFrame(completionStop.ThreadId!.Value);
		completionStackFrame.Source.Path.Should().Be(programPath);
		completionStackFrame.Line.Should().Be(completionMarkerLine);
		completionStop.Reason.Should().Be(StoppedEvent.ReasonValue.Breakpoint);
	}
}
