using AwesomeAssertions;
using DotnetDbg.Cli.Tests.Helpers;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace DotnetDbg.Cli.Tests;

public class EvalTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
    public async Task DotnetDbgCli_EvaluationRequest_Returns()
    {
	    var startSuspended = false;

	    var (debugProtocolHost, initializedEventTcs, stoppedEventTcs, adapter, p2) = TestHelper.GetRunningDebugProtocolHostInProc(testOutputHelper);
	    using var _ = adapter;
	    using var __ = new ProcessKiller(p2);

	    await debugProtocolHost
		    .WithInitializeRequest()
		    .WithAttachRequest(p2.Id)
		    .WaitForInitializedEvent(initializedEventTcs);
	    debugProtocolHost
		    .WithBreakpointsRequest()
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
		    new() {Name = "this", Value = "{DebuggableConsoleApp.MyClass}", Type = "DebuggableConsoleApp.MyClass", EvaluateName = "this", VariablesReference = 3 },
		    new() {Name = "myParam", Value = "13", Type = "long", EvaluateName = "myParam" },
		    new() {Name = "myInt", Value = "4", Type = "int", EvaluateName = "myInt" },
		    new() {Name = "enumVar", Value = "SecondValue", Type = "DebuggableConsoleApp.MyEnum", EvaluateName = "enumVar", VariablesReference = 4 },
		    new() {Name = "enumWithFlagsVar", Value = "FlagValue1 | FlagValue3", Type = "DebuggableConsoleApp.MyEnumWithFlags", EvaluateName = "enumWithFlagsVar", VariablesReference = 5 },
		    new() {Name = "nullableInt", Value = "null", Type = "int?", EvaluateName = "nullableInt" },
		    new() {Name = "structVar", Value = "{DebuggableConsoleApp.MyStruct}", Type = "DebuggableConsoleApp.MyStruct", EvaluateName = "structVar", VariablesReference = 6 },
		    new() {Name = "nullableIntWithVal", Value = "4", Type = "int?", EvaluateName = "nullableIntWithVal" },
		    new() {Name = "nullableRefType", Value = "null", Type = "DebuggableConsoleApp.MyClass", EvaluateName = "nullableRefType" },
		    new() {Name = "anotherVar", Value = "asdf", Type = "string", EvaluateName = "anotherVar" },
	    ];

	    debugProtocolHost.WithVariablesRequest(scope.VariablesReference, out var variables);

	    variables.Should().HaveCount(11);
	    //variables.Should().BeEquivalentTo(expectedVariables);

	    var stackFrameId = stackTraceResponse.StackFrames!.First().Id;
	    debugProtocolHost.WithEvaluateRequest(stackFrameId, "myInt + 10", out var evaluateResponse);
	    evaluateResponse.Result.Should().Be("14");
	    debugProtocolHost.WithEvaluateRequest(stackFrameId, "myInt + myInt", out var evaluateResponse2);
	    evaluateResponse2.Result.Should().Be("8");
	    debugProtocolHost.WithEvaluateRequest(stackFrameId, "myIntParam + 4", out var evaluateResponse3);
	    evaluateResponse3.Result.Should().Be("10");
	    debugProtocolHost.WithEvaluateRequest(stackFrameId, "_instanceField + 4", out var evaluateResponse4);
	    evaluateResponse4.Result.Should().Be("9");
	    debugProtocolHost.WithEvaluateRequest(stackFrameId, "_instanceStaticField + 4", out var evaluateResponse5);
	    evaluateResponse5.Result.Should().Be("10");
	    // netcoredbg currently does not support assignment via eval, and thus dotnetdbg also does not support it yet
	    // debugProtocolHost.WithEvaluateRequest(stackFrameId, "_instanceStaticField = _instanceStaticField + 4", out var evaluateResponse6);
	    // evaluateResponse6.Result.Should().Be("10");
	    // debugProtocolHost.WithEvaluateRequest(stackFrameId, "_instanceStaticField", out var evaluateResponse7);
	    // evaluateResponse7.Result.Should().Be("10");
	    debugProtocolHost.WithEvaluateRequest(stackFrameId, "IntProperty + 4", out var evaluateResponse8);
	    evaluateResponse8.Result.Should().Be("14");
	    debugProtocolHost.WithEvaluateRequest(stackFrameId, "IntStaticProperty + 4", out var evaluateResponse9);
	    evaluateResponse9.Result.Should().Be("14");
	    debugProtocolHost.WithEvaluateRequest(stackFrameId, "ClassProperty.IntField + 4", out var evaluateResponse10);
	    evaluateResponse10.Result.Should().Be("10");
	    debugProtocolHost.WithEvaluateRequest(stackFrameId, "this.Get14() + 4", out var evaluateResponse11);
	    evaluateResponse11.Result.Should().Be("18");
	    debugProtocolHost.WithEvaluateRequest(stackFrameId, "MyClass.IntStaticProperty + 4", out var evaluateResponse12);
	    evaluateResponse12.Result.Should().Be("14");
	    debugProtocolHost.WithEvaluateRequest(stackFrameId, "DebuggableConsoleApp.MyClass.IntStaticProperty + 4", out var evaluateResponse13);
	    evaluateResponse13.Result.Should().Be("14");
	    debugProtocolHost.WithEvaluateRequest(stackFrameId, "Namespace1.AnotherClass.IntStaticProperty + 4", out var evaluateResponse14);
	    evaluateResponse14.Result.Should().Be("14");
	    debugProtocolHost.WithEvaluateRequest(stackFrameId, "this.DoubleNumber(4)", out var evaluateResponse15);
	    evaluateResponse15.Result.Should().Be("8");
	    debugProtocolHost.WithEvaluateRequest(stackFrameId, "this.DoubleNumber(4f)", out var evaluateResponse16);
	    evaluateResponse16.Result.Should().Be("8");
	    // TODO: Fix
	    //debugProtocolHost.WithEvaluateRequest(stackFrameId, "Get14()", out var evaluateResponse16);
	    //evaluateResponse16.Result.Should().Be("14");
    }
}
