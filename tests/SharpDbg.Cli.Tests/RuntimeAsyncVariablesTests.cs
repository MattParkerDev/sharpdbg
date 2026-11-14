using AwesomeAssertions;
using SharpDbg.DapProtocol;
using SharpDbg.Cli.Tests.Helpers;

namespace SharpDbg.Cli.Tests;

public class RuntimeAsyncVariablesTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task AsyncMethod_VariablesRequest_ReturnsCorrectVariables()
	{
		var startSuspended = true;

		var (debugProtocolHost, adapter, p2) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended, DebuggableProcessHelper.RuntimeAsyncConsoleApp);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(p2.Id)
			.WaitForInitializedEvent();
		debugProtocolHost
			.WithBreakpointsRequest(11, Path.JoinFromGitRoot("tests", "RuntimeAsyncConsoleApp", "MyAsyncClass.cs"))
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
		debugProtocolHost
			.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTraceResponse)
			.WithScopesRequest(stackTraceResponse.StackFrames!.First().Id, out var scopesResponse);

		scopesResponse.Scopes.Should().HaveCount(1);
		var scope = scopesResponse.Scopes.Single();

		List<Variable> expectedVariables =
		[
			new() {Name = "this", Value = "{RuntimeAsyncConsoleApp.MyAsyncClass}", Type = "RuntimeAsyncConsoleApp.MyAsyncClass", EvaluateName = "this", VariablesReference = 2 },
			new() {Name = "myParam", Value = "4", Type = "int", EvaluateName = "myParam" },
			new() {Name = "intVar", Value = "10", Type = "int", EvaluateName = "intVar" },
			new() {Name = "result", Value = "0", Type = "int", EvaluateName = "result" },
			new() {Name = "result2", Value = "0", Type = "int", EvaluateName = "result2" },
			new() {Name = "result3", Value = "0", Type = "int", EvaluateName = "result3" },

		];

		debugProtocolHost.WithVariablesRequest(scope.VariablesReference, out var variables);

		variables.Should().HaveCount(6);
		variables.ShouldBeEquivalentToDebuggerVariables(expectedVariables);

		var stoppedEvent2 = await debugProtocolHost.WithStepInRequest(stoppedEvent.ThreadId!.Value).WaitForStoppedEvent();
		var stopInfo = stoppedEvent2.ReadStopInfo();
		stopInfo.filePath.Should().EndWith("AnotherClass.cs");
		stopInfo.line.Should().Be(18);

		debugProtocolHost
			.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTraceResponse2)
			.WithScopesRequest(stackTraceResponse2.StackFrames!.First().Id, out var scopesResponse2)
			.WithVariablesRequest(scopesResponse2.Scopes.Single().VariablesReference, out var variables2);

		List<Variable> staticAsyncMethodExpectedVariables =
		[
			new() {Name = "test", Value = "0", Type = "int", EvaluateName = "test" },
		];

		variables2.ShouldBeEquivalentToDebuggerVariables(staticAsyncMethodExpectedVariables);

		var stoppedEvent3 = await debugProtocolHost
			.WithContinueRequest()
			.WaitForStoppedEvent();
		debugProtocolHost
			.WithStackTraceRequest(stoppedEvent3.ThreadId!.Value, out var stackTraceResponse3)
			.WithScopesRequest(stackTraceResponse3.StackFrames!.First().Id, out var scopesResponse3)
			.WithVariablesRequest(scopesResponse3.Scopes.Single().VariablesReference, out var variables3);
		// The debuggee loops back here; variable references must reset on continue, so the same IDs should be assigned again
		variables3.ShouldBeEquivalentToDebuggerVariables(expectedVariables);
	}
}

file static class TestExtensions
{

}
