using AwesomeAssertions;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using SharpDbg.Cli.Tests.Helpers;

namespace SharpDbg.Cli.Tests;

public class AsyncVariablesTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
    public async Task AsyncMethod_VariablesRequest_ReturnsCorrectVariables()
    {
	    var startSuspended = true;

	    var (debugProtocolHost, initializedEventTcs, stoppedEventTcs, adapter, p2) = TestHelper.GetRunningDebugProtocolHostInProc(testOutputHelper, startSuspended);
	    using var _ = adapter;
	    using var __ = new ProcessKiller(p2);

	    await debugProtocolHost
		    .WithInitializeRequest()
		    .WithAttachRequest(p2.Id)
		    .WaitForInitializedEvent(initializedEventTcs);
	    debugProtocolHost
		    .WithBreakpointsRequest(11, Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "MyAsyncClass.cs"))
		    .WithConfigurationDoneRequest()
		    .WithOptionalResumeRuntime(p2.Id, startSuspended);

	    var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(stoppedEventTcs);
	    debugProtocolHost
		    .WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTraceResponse)
		    .WithScopesRequest(stackTraceResponse.StackFrames!.First().Id, out var scopesResponse);

	    scopesResponse.Scopes.Should().HaveCount(1);
	    var scope = scopesResponse.Scopes.Single();

	    List<Variable> expectedVariables =
	    [
		    new() {Name = "this", Value = "{DebuggableConsoleApp.MyAsyncClass}", Type = "DebuggableConsoleApp.MyAsyncClass", EvaluateName = "this", VariablesReference = 3 },
		    new() {Name = "myParam", Value = "4", Type = "int", EvaluateName = "myParam" },
		    new() {Name = "intVar", Value = "10", Type = "int", EvaluateName = "intVar" },
		    new() {Name = "result", Value = "0", Type = "int", EvaluateName = "result" },
		    new() {Name = "result2", Value = "0", Type = "int", EvaluateName = "result2" },
		    new() {Name = "result3", Value = "0", Type = "int", EvaluateName = "result3" },

	    ];

	    debugProtocolHost.WithVariablesRequest(scope.VariablesReference, out var variables);

	    variables.Should().HaveCount(6);
	    variables.Should().BeEquivalentTo(expectedVariables);


	    var stoppedEvent2 = await debugProtocolHost
		    .WithContinueRequest()
		    .WaitForStoppedEvent(stoppedEventTcs);
	    debugProtocolHost
		    .WithStackTraceRequest(stoppedEvent2.ThreadId!.Value, out var stackTraceResponse2)
		    .WithScopesRequest(stackTraceResponse2.StackFrames!.First().Id, out var scopesResponse2)
		    .WithVariablesRequest(scopesResponse2.Scopes.Single().VariablesReference, out var variables2);
	    // Assert the variables reference count resets on continue, by asserting the variables are the same as the first time (code is in a while loop)
	    variables2.Should().BeEquivalentTo(expectedVariables);
    }
}

file static class TestExtensions
{

}
