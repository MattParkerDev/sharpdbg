using AwesomeAssertions;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using SharpDbg.Cli.Tests.Helpers;

namespace SharpDbg.Cli.Tests;

public class LambdaVariablesTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
    public async Task SharpDbgCli_InLambda_VariablesRequest_Returns_InScopeVariables()
    {
	    var startSuspended = false;
	    var (debugProtocolHost, initializedEventTcs, stoppedEventTcs, adapter, p2) = TestHelper.GetRunningDebugProtocolHostInProc(testOutputHelper, startSuspended);
	    using var _ = adapter;
	    using var __ = new ProcessKiller(p2);

	    await debugProtocolHost
		    .WithInitializeRequest()
		    .WithAttachRequest(p2.Id)
		    .WaitForInitializedEvent(initializedEventTcs);
	    debugProtocolHost
		    .WithBreakpointsRequest([11], Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "Lambdas", "MyLambdaClass.cs"))
		    .WithConfigurationDoneRequest()
		    .WithOptionalResumeRuntime(p2.Id, startSuspended);

	    // stop inside the lambda
	    var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(stoppedEventTcs);
	    var stopInfo = stoppedEvent.ReadStopInfo();
	    stopInfo.filePath.Should().EndWith("MyLambdaClass.cs");
	    stopInfo.line.Should().Be(11);

	    debugProtocolHost
		    .WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTraceResponse)
		    .WithScopesRequest(stackTraceResponse.StackFrames!.First().Id, out var scopesResponse);

	    scopesResponse.Scopes.Should().HaveCount(1);
	    var scope = scopesResponse.Scopes.Single();

	    List<Variable> expectedVariables =
	    [
		    new() {Name = "this", Value = "{DebuggableConsoleApp.Lambdas.MyLambdaClass}", Type = "DebuggableConsoleApp.Lambdas.MyLambdaClass", EvaluateName = "this", VariablesReference = 3 },
		    new() {Name = "capturedIntField", EvaluateName = "capturedIntField", Value = "4",  Type = "int" },
		    new() {Name = "capturedString",  EvaluateName = "capturedString",  Value = "asdf", Type = "string" },
		    new() {Name = "result",  EvaluateName = "result",  Value = "0",  Type = "int" },
		    new() {Name = "test", EvaluateName = "test", Value = "asdf",  Type = "string" },
		    new() {Name = "x", EvaluateName = "x", Value = "5",  Type = "int" },

	    ];

	    debugProtocolHost.WithVariablesRequest(scope.VariablesReference, out var variables);

	    variables.Should().HaveCount(6);
	    variables.Should().BeEquivalentTo(expectedVariables);
    }
}
