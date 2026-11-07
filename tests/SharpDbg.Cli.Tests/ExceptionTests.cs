using AwesomeAssertions;
using SharpDbg.DapProtocol;
using SharpDbg.Cli.Tests.Helpers;

namespace SharpDbg.Cli.Tests;

public class ExceptionTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task SharpDbgCli_Exception_VariablesHasExceptionScope()
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
		debugProtocolHost.SendRequestSync(new SetExceptionBreakpointsArguments { Filters = [], FilterOptions = [new() { FilterId = "all" }, new() { FilterId = "user-unhandled" }] });
		var breakpointedFilePath = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "Exceptions.cs");
		debugProtocolHost
			.WithBreakpointsRequest([24], Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "Program.cs"))
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
		var stopInfo = stoppedEvent.ReadStopInfo();
		stopInfo.filePath.Should().EndWith("Program.cs");
		stopInfo.line.Should().Be(24);

		// set 'ExceptionToThrow' to .Normal - we do not want other tests to stop at the 'exception' stop event, only this one
		debugProtocolHost.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTraceResponse);
		debugProtocolHost.WithEvaluateRequest(stackTraceResponse.StackFrames.First().Id, "exceptionToThrow = ExceptionToThrow.Normal", out var evaluateResponse);
		evaluateResponse.Result.Should().Be("Normal");

		debugProtocolHost.WithContinueRequest();

		var stoppedEvent2 = await debugProtocolHost.WaitForStoppedEvent();
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
		variables.ShouldBeEquivalentToDebuggerVariables(expectedVariables);

		debugProtocolHost.WithEvaluateRequest(stackTraceResponse.StackFrames.First().Id, "$exception", out var evaluateResponse2);
		evaluateResponse2.Result.Should().Be(expectedVariables[0].Value);

		var expectedExceptionInfoResponse = new ExceptionInfoResponse
		{
			ExceptionId = "CLR/System.InvalidOperationException",
			Description = "Exception thrown: 'System.InvalidOperationException' in DebuggableConsoleApp.dll: 'Test exception'",
			BreakMode = ExceptionBreakMode.Always,
			Details = new ExceptionDetails
			{
				Message = "Test exception",
				TypeName = "InvalidOperationException",
				FullTypeName = "System.InvalidOperationException",
				EvaluateName = "$exception",
				StackTrace = $"   at DebuggableConsoleApp.Exceptions.Test(ExceptionToThrow exceptionToThrow) in {breakpointedFilePath}:line 14",
				InnerException = null
			}
		};

		var exceptionInfoResponse = debugProtocolHost.SendRequestSync(new ExceptionInfoArguments { ThreadId = stoppedEvent2.ThreadId.Value });
		exceptionInfoResponse.Should().BeEquivalentTo(expectedExceptionInfoResponse, options => options.Excluding(r => r.Details!.AdditionalProperties));
		exceptionInfoResponse.AdditionalProperties.Should().BeNull(); // 'code' is omitted when 0
		exceptionInfoResponse.Details.ShouldHaveVisualStudioExceptionDetails("**System.InvalidOperationException:** 'Test exception'", -2146233079, "DebuggableConsoleApp");
	}

	[Fact]
	public async Task ExceptionInExternalCode_JustMyCodeEnabled_HasNoSourceInfo()
	{
		var startSuspended = true;
		var (debugProtocolHost, adapter, p2) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(p2.Id, true)
			.WaitForInitializedEvent();
		debugProtocolHost.SendRequestSync(new SetExceptionBreakpointsArguments { Filters = [], FilterOptions = [new() { FilterId = "all" }, new() { FilterId = "user-unhandled" }] });
		var breakpointedFilePath = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "Exceptions.cs");
		debugProtocolHost
			.WithBreakpointsRequest([24], Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "Program.cs"))
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
		var stopInfo = stoppedEvent.ReadStopInfo();
		stopInfo.filePath.Should().EndWith("Program.cs");
		stopInfo.line.Should().Be(24);

		// set 'ExceptionToThrow' to .ExternalCode - we do not want other tests to stop at the 'exception' stop event, only this one
		debugProtocolHost.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTraceResponse);
		debugProtocolHost.WithEvaluateRequest(stackTraceResponse.StackFrames.First().Id, "exceptionToThrow = ExceptionToThrow.ExternalCode", out var evaluateResponse);
		evaluateResponse.Result.Should().Be("ExternalCode");

		debugProtocolHost.WithContinueRequest();

		var stoppedEvent2 = await debugProtocolHost.WaitForStoppedEvent();
		stoppedEvent2.AdditionalProperties.Should().BeNull();

		debugProtocolHost
			.WithStackTraceRequest(stoppedEvent2.ThreadId!.Value, out var stackTraceResponse2, null)
			.WithScopesRequest(stackTraceResponse2.StackFrames!.First().Id, out var scopesResponse2);

		List<StackFrame> expectedStackFrames =
		[
			new() { Id = 1, Column = 0, EndColumn =  null, Line =  0, EndLine =  null, Name = "System.Private.CoreLib.dll!System.Number.ThrowFormatException<char>(System.ReadOnlySpan<char> value)", Source = null, PresentationHint = StackFrame.PresentationHintValue.Subtle },
			new() { Id = 2, Column = 0, EndColumn =  null, Line =  0, EndLine =  null, Name = "System.Private.CoreLib.dll!System.Int32.Parse(string s)",                 Source = null, PresentationHint = StackFrame.PresentationHintValue.Subtle },
			new() { Id = 3, Column = 5, EndColumn = 32,    Line = 18, EndLine = 18,    Name = "DebuggableConsoleApp.dll!DebuggableConsoleApp.Exceptions.Test(DebuggableConsoleApp.ExceptionToThrow exceptionToThrow)", Source = new Source { Name = "Exceptions.cs", SourceReference = 0, Path = breakpointedFilePath } },
			new() { Id = 4, Column = 4, EndColumn = 38,    Line = 34, EndLine = 34,    Name = "DebuggableConsoleApp.dll!DebuggableConsoleApp.Program.Main(string[] args)",    Source = new Source { Name = "Program.cs",    SourceReference = 0, Path = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "Program.cs") } },
		];

		stackTraceResponse2.StackFrames.Should().BeEquivalentTo(expectedStackFrames, options => options.Excluding(s => s.InstructionPointerReference).Excluding(s => s.Source!.Checksums).Excluding(s => s.AdditionalProperties));
		scopesResponse2.Scopes.Should().HaveCount(1);
		var scope = scopesResponse2.Scopes.Single();

		var expectedStackTrace = $"   at System.Number.ThrowFormatException[TChar](ReadOnlySpan`1 value){Environment.NewLine}   at System.Int32.Parse(String s){Environment.NewLine}   at DebuggableConsoleApp.Exceptions.Test(ExceptionToThrow exceptionToThrow) in {breakpointedFilePath}:line 18";
		List<Variable> expectedVariables =
		[
			new() { Name = "$exception",  EvaluateName = "$exception",  Value = $"System.FormatException: The input string 'x' was not in a correct format.{Environment.NewLine}{expectedStackTrace}", Type = "System.FormatException", VariablesReference = 2 }
		];
		debugProtocolHost.WithVariablesRequest(scope.VariablesReference, out var variables);

		variables.Should().HaveCount(expectedVariables.Count);
		variables.ShouldBeEquivalentToDebuggerVariables(expectedVariables);

		debugProtocolHost.WithEvaluateRequest(stackTraceResponse.StackFrames.First().Id, "$exception", out var evaluateResponse2);
		evaluateResponse2.Result.Should().Be(expectedVariables[0].Value);

		var expectedExceptionInfoResponse = new ExceptionInfoResponse
		{
			ExceptionId = "CLR/System.FormatException",
			Description = "Exception thrown: 'System.FormatException' in System.Private.CoreLib.dll: 'The input string 'x' was not in a correct format.'",
			BreakMode = ExceptionBreakMode.Always,
			Details = new ExceptionDetails
			{
				Message = "The input string 'x' was not in a correct format.",
				TypeName = "FormatException",
				FullTypeName = "System.FormatException",
				EvaluateName = "$exception",
				StackTrace = expectedStackTrace,
				InnerException = null
			}
		};

		var exceptionInfoResponse = debugProtocolHost.SendRequestSync(new ExceptionInfoArguments { ThreadId = stoppedEvent2.ThreadId.Value });
		exceptionInfoResponse.Should().BeEquivalentTo(expectedExceptionInfoResponse, options => options.Excluding(r => r.Details!.AdditionalProperties));
		exceptionInfoResponse.AdditionalProperties.Should().BeNull(); // 'code' is omitted when 0
		exceptionInfoResponse.Details.ShouldHaveVisualStudioExceptionDetails("**System.FormatException:** 'The input string 'x' was not in a correct format.'", -2146233033, "System.Private.CoreLib");

		// Now we should land on the catch block
		var stoppedEvent3 = await debugProtocolHost.WithStepOverRequest(stoppedEvent2.ThreadId!.Value).WaitForStoppedEvent();
		var stopInfo3 = stoppedEvent3.ReadStopInfo();
		stopInfo3.Should().Be((breakpointedFilePath, 37, 3));
	}

	[Fact]
	public Task ExceptionFilters_BreakOnAllExceptions()
	{
		return AssertBreaksOnException(
			new SetExceptionBreakpointsArguments { Filters = ["all"], FilterOptions = [] },
			exceptionToThrow: "Normal",
			justMyCode: true,
			expectedExceptionType: "System.InvalidOperationException",
			expectedBreakMode: ExceptionBreakMode.Always);
	}

	[Fact]
	public Task ExceptionFilters_AllExceptionsWithJmcEnabled_DoesNotBreakWhenHandledInExternalCode()
	{
		return AssertContinuesWithoutExceptionStop(
			new SetExceptionBreakpointsArguments { Filters = ["all"], FilterOptions = [] },
			exceptionToThrow: "HandledWithinExternalCode",
			justMyCode: true);
	}

	[Fact]
	public Task ExceptionFilters_AllExceptionsWithJmcDisabled_BreaksWhenHandledInExternalCode()
	{
		return AssertBreaksOnException(
			new SetExceptionBreakpointsArguments { Filters = ["all"], FilterOptions = [] },
			exceptionToThrow: "HandledWithinExternalCode",
			justMyCode: false,
			expectedExceptionType: "System.ArgumentException",
			expectedBreakMode: ExceptionBreakMode.Always);
	}

	[Fact]
	public Task ExceptionFilters_AllExceptionsWithJmcEnabled_BreaksWhenReturnedToUserCode()
	{
		return AssertBreaksOnException(
			new SetExceptionBreakpointsArguments { Filters = ["all"], FilterOptions = [] },
			exceptionToThrow: "ExternalCode",
			justMyCode: true,
			expectedExceptionType: "System.FormatException",
			expectedBreakMode: ExceptionBreakMode.Always);
	}

	[Fact]
	public Task ExceptionFilters_BreakOnUserUnhandledExceptions()
	{
		return AssertBreaksOnException(
			new SetExceptionBreakpointsArguments { Filters = ["user-unhandled"], FilterOptions = [] },
			exceptionToThrow: "UserUnhandled",
			justMyCode: true,
			expectedExceptionType: "System.InvalidOperationException",
			expectedBreakMode: ExceptionBreakMode.UserUnhandled);
	}

	[Fact]
	public Task ExceptionFilters_UserUnhandledDoesNotBreakOnExceptionHandledInUserCode()
	{
		return AssertContinuesWithoutExceptionStop(
			new SetExceptionBreakpointsArguments { Filters = ["user-unhandled"], FilterOptions = [] },
			exceptionToThrow: "Normal",
			justMyCode: true);
	}

	[Theory]
	[InlineData("System.InvalidOperationException", true)]
	[InlineData("System.FormatException", false)]
	public Task ExceptionFilters_UserUnhandledConditionFiltersExceptionTypes(string condition, bool expectExceptionStop)
	{
		var request = new SetExceptionBreakpointsArguments
		{
			Filters = [],
			FilterOptions = [new ExceptionFilterOptions { FilterId = "user-unhandled", Condition = condition }]
		};

		return expectExceptionStop
			? AssertBreaksOnException(request, exceptionToThrow: "UserUnhandled", justMyCode: true, expectedExceptionType: "System.InvalidOperationException", expectedBreakMode: ExceptionBreakMode.UserUnhandled)
			: AssertContinuesWithoutExceptionStop(request, exceptionToThrow: "UserUnhandled", justMyCode: true);
	}

	[Fact]
	public Task ExceptionFilters_NoFiltersDoesNotBreakOnHandledException()
	{
		return AssertContinuesWithoutExceptionStop(
			new SetExceptionBreakpointsArguments { Filters = [], FilterOptions = [] },
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

	[Fact]
	public async Task ExceptionInfo_WrappedException_ReportsInnerExceptionChain()
	{
		var exceptionInfo = await AssertBreaksOnException(
			new SetExceptionBreakpointsArguments { Filters = [], FilterOptions = [new ExceptionFilterOptions { FilterId = "all", Condition = "System.InvalidOperationException" }] },
			exceptionToThrow: "Inner",
			justMyCode: true,
			expectedExceptionType: "System.InvalidOperationException",
			expectedBreakMode: ExceptionBreakMode.Always);

		exceptionInfo.Description.Should().Be("Exception thrown: 'System.InvalidOperationException' in DebuggableConsoleApp.dll: 'Outer exception'\n Inner exceptions found, see $exception in variables window for more details.\n Innermost exception \t System.Exception : Innermost exception");
		exceptionInfo.Details!.Message.Should().Be("Outer exception");

		var inner = exceptionInfo.Details.InnerException.Should().ContainSingle().Subject;
		inner.FullTypeName.Should().Be("System.FormatException");
		inner.TypeName.Should().Be("FormatException");
		inner.Message.Should().Be("Inner exception");
		inner.EvaluateName.Should().Be("$exception");
		inner.ShouldHaveVisualStudioExceptionDetails("**System.FormatException:** 'Inner exception'", -2146233033, "DebuggableConsoleApp");

		var innermost = inner.InnerException.Should().ContainSingle().Subject;
		innermost.FullTypeName.Should().Be("System.Exception");
		innermost.Message.Should().Be("Innermost exception");
		innermost.InnerException.Should().BeNull();
	}

	private Task AssertAllExceptionsCondition(string condition, bool expectExceptionStop)
	{
		var request = new SetExceptionBreakpointsArguments
		{
			Filters = [],
			FilterOptions = [new ExceptionFilterOptions { FilterId = "all", Condition = condition }]
		};

		return expectExceptionStop
			? AssertBreaksOnException(request, exceptionToThrow: "Normal", justMyCode: true, expectedExceptionType: "System.InvalidOperationException", expectedBreakMode: ExceptionBreakMode.Always)
			: AssertContinuesWithoutExceptionStop(request, exceptionToThrow: "Normal", justMyCode: true);
	}

	private async Task<ExceptionInfoResponse> AssertBreaksOnException(
		SetExceptionBreakpointsArguments exceptionBreakpointsRequest,
		string exceptionToThrow,
		bool justMyCode,
		string expectedExceptionType,
		ExceptionBreakMode expectedBreakMode)
	{
		const bool startSuspended = true;
		var (debugProtocolHost, adapter, process) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended);
		using var _ = adapter;
		using var __ = new ProcessKiller(process);
		using var ___ = debugProtocolHost;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(process.Id, justMyCode)
			.WaitForInitializedEvent();
		debugProtocolHost.SendRequestSync(exceptionBreakpointsRequest);

		const int setupBreakpointLine = 24;
		const int completionMarkerLine = 35;
		var programPath = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "Program.cs");
		debugProtocolHost
			.WithBreakpointsRequest([setupBreakpointLine], programPath)
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(process.Id, startSuspended);

		var setupStop = await debugProtocolHost.WaitForStoppedEvent();
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

		var exceptionStop = await debugProtocolHost.WaitForStoppedEvent();
		exceptionStop.Reason.Should().Be(StoppedEvent.ReasonValue.Exception);

		var exceptionInfo = debugProtocolHost.SendRequestSync(new ExceptionInfoArguments { ThreadId = exceptionStop.ThreadId!.Value });
		exceptionInfo.Details!.FullTypeName.Should().Be(expectedExceptionType);
		exceptionInfo.BreakMode.Should().Be(expectedBreakMode);
		return exceptionInfo;
	}

	private async Task AssertContinuesWithoutExceptionStop(
		SetExceptionBreakpointsArguments exceptionBreakpointsRequest,
		string exceptionToThrow,
		bool justMyCode)
	{
		const bool startSuspended = true;
		var (debugProtocolHost, adapter, process) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended);
		using var _ = adapter;
		using var __ = new ProcessKiller(process);
		using var ___ = debugProtocolHost;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(process.Id, justMyCode)
			.WaitForInitializedEvent();
		debugProtocolHost.SendRequestSync(exceptionBreakpointsRequest);

		const int setupBreakpointLine = 24;
		const int completionMarkerLine = 35;
		var programPath = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "Program.cs");
		debugProtocolHost
			.WithBreakpointsRequest([setupBreakpointLine], programPath)
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(process.Id, startSuspended);

		var setupStop = await debugProtocolHost.WaitForStoppedEvent();
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

		var completionStop = await debugProtocolHost.WaitForStoppedEvent();
		var completionStackFrame = debugProtocolHost.GetTopStackFrame(completionStop.ThreadId!.Value);
		completionStackFrame.Source!.Path.Should().Be(programPath);
		completionStackFrame.Line.Should().Be(completionMarkerLine);
		completionStop.Reason.Should().Be(StoppedEvent.ReasonValue.Breakpoint);
	}
}

file static class ExceptionDetailsAssertions
{
	/// <summary>Asserts the Visual Studio extensions to <see cref="ExceptionDetails"/>, which are sent as additional properties.</summary>
	public static void ShouldHaveVisualStudioExceptionDetails(this ExceptionDetails? details, string formattedDescription, int hResult, string source)
	{
		var additionalProperties = details!.AdditionalProperties!;
		additionalProperties["formattedDescription"]!.GetValue<string>().Should().Be(formattedDescription);
		additionalProperties["hresult"]!.GetValue<int>().Should().Be(hResult);
		additionalProperties["source"]!.GetValue<string>().Should().Be(source);
	}
}
