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
		debugProtocolHost.SendRequestSync(new SetExceptionBreakpointsRequest { Filters = [], FilterOptions = [new("all"), new ("user-unhandled")]});
		var breakpointedFilePath = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "Exceptions.cs");
		debugProtocolHost
			.WithBreakpointsRequest([23], Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "Program.cs"))
			.WithBreakpointsRequest([17], breakpointedFilePath)
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(debugEventTcs);
		var stopInfo = stoppedEvent.ReadStopInfo();
		stopInfo.filePath.Should().EndWith("Program.cs");
		stopInfo.line.Should().Be(23);

		// set 'throwException' to true - we do not want other tests to stop at the 'exception' stop event, only this one
		debugProtocolHost.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTraceResponse);
		debugProtocolHost.WithEvaluateRequest(stackTraceResponse.StackFrames.First().Id, "throwException = true", out var evaluateResponse);
		evaluateResponse.Result.Should().Be("true");

		debugProtocolHost.WithContinueRequest();

		var stoppedEvent2 = await debugProtocolHost.WaitForStoppedEvent(debugEventTcs);
		var stopInfo2 = stoppedEvent2.ReadStopInfo();
		stopInfo2.filePath.Should().EndWith("Exceptions.cs");
		stopInfo2.line.Should().Be(12); // Where the exception is thrown

		debugProtocolHost
			.WithStackTraceRequest(stoppedEvent2.ThreadId!.Value, out var stackTraceResponse2)
			.WithScopesRequest(stackTraceResponse2.StackFrames!.First().Id, out var scopesResponse2);

		scopesResponse2.Scopes.Should().HaveCount(1);
		var scope = scopesResponse2.Scopes.Single();

		List<Variable> expectedVariables =
		[
			new() { Name = "$exception",  EvaluateName = "$exception",  Value = $$"""System.InvalidOperationException: Test exception{{"\r\n"}}   at DebuggableConsoleApp.Exceptions.Test(Boolean shouldThrow) in {{breakpointedFilePath}}:line 12""", Type = "System.InvalidOperationException", VariablesReference = 4 },
			new() { Name = "shouldThrow", EvaluateName = "shouldThrow", Value = "true",  Type = "bool" },
			new() { Name = "test", EvaluateName = "test", Value = "true",  Type = "bool" },
		];
		debugProtocolHost.WithVariablesRequest(scope.VariablesReference, out var variables);

		variables.Should().HaveCount(3);
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
				StackTrace = $"   at DebuggableConsoleApp.Exceptions.Test(Boolean shouldThrow) in {breakpointedFilePath}:line 12",
				InnerException = [],
				FormattedDescription = "**System.InvalidOperationException:** 'Test exception'",
				HResult = -2146233079,
				Source = "DebuggableConsoleApp"
			}
		};

		var exceptionInfoResponse = debugProtocolHost.SendRequestSync(new ExceptionInfoRequest(stoppedEvent2.ThreadId.Value));
		exceptionInfoResponse.Should().BeEquivalentTo(expectedExceptionInfoResponse);
	}
}
