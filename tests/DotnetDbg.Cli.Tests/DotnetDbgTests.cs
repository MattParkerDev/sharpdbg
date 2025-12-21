using System.Threading.Channels;
using AwesomeAssertions;
using DotnetDbg.Cli.Tests.Helpers;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace DotnetDbg.Cli.Tests;

public class DotnetDbgTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task DotnetDbgCli_InitializeRequest_Returns()
    {
	    var process = DebugAdapterProcessHelper.GetDebugAdapterProcess();
	    var debugProtocolHost = DebugAdapterProcessHelper.GetDebugProtocolHost(process, testOutputHelper);
	    debugProtocolHost.Run();
	    var initializeRequest = DebugAdapterProcessHelper.GetInitializeRequest();

	    InitializeResponse? response = null;
	    Task.RunWithTimeout(() => response = debugProtocolHost.SendRequestSync(initializeRequest), () => process.Kill());

	    process.Kill();
	    var settings = new VerifySettings();
	    //settings.AutoVerify();

	    await Verify(response, settings);
    }

    [Fact]
    public async Task DotnetDbgCli_AttachRequest_Returns()
    {
	    var process = DebugAdapterProcessHelper.GetDebugAdapterProcess();
	    var debuggableProcess = DebuggableProcessHelper.StartDebuggableProcess();
	    try
	    {
		    var debugProtocolHost = DebugAdapterProcessHelper.GetDebugProtocolHost(process, testOutputHelper);
			debugProtocolHost.Run();
		    var initializeRequest = DebugAdapterProcessHelper.GetInitializeRequest();
		    debugProtocolHost.SendRequestSync(initializeRequest);
		    var attachRequest = DebugAdapterProcessHelper.GetAttachRequest(debuggableProcess.Id);
		    debugProtocolHost.SendRequestSync(attachRequest);
	    }
	    finally
	    {
		    process.Kill();
		    debuggableProcess.Kill();
	    }
    }

    [Fact]
    public async Task DotnetDbgCli_SetBreakpointsRequest_Returns()
    {
	    var process = DebugAdapterProcessHelper.GetDebugAdapterProcess();
	    var debuggableProcess = DebuggableProcessHelper.StartDebuggableProcess(false);
	    try
	    {
			var initializedEventTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		    var debugProtocolHost = DebugAdapterProcessHelper.GetDebugProtocolHost(process, testOutputHelper, initializedEventTcs);
			debugProtocolHost.Run();
		    var initializeRequest = DebugAdapterProcessHelper.GetInitializeRequest();
		    debugProtocolHost.SendRequestSync(initializeRequest);
		    var attachRequest = DebugAdapterProcessHelper.GetAttachRequest(debuggableProcess.Id);
		    debugProtocolHost.SendRequestSync(attachRequest);
		    await initializedEventTcs.Task;
		    var setBreakpointsRequest = DebugAdapterProcessHelper.GetSetBreakpointsRequest();
		    var breakpointsResponse = debugProtocolHost.SendRequestSync(setBreakpointsRequest);
		    await Verify(breakpointsResponse);
	    }
	    finally
	    {
		    process.Kill();
		    debuggableProcess.Kill();
	    }
    }

    [Fact]
    public async Task DotnetDbgCli_ConfigurationDoneRequest_Returns()
    {
	    var startSuspended = false;
	    var process = DebugAdapterProcessHelper.GetDebugAdapterProcess();
	    var debuggableProcess = DebuggableProcessHelper.StartDebuggableProcess(startSuspended);
	    try
	    {
		    var initializedEventTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		    var debugProtocolHost = DebugAdapterProcessHelper.GetDebugProtocolHost(process, testOutputHelper, initializedEventTcs);
			debugProtocolHost.Run();
		    var initializeRequest = DebugAdapterProcessHelper.GetInitializeRequest();
		    debugProtocolHost.SendRequestSync(initializeRequest);
		    var attachRequest = DebugAdapterProcessHelper.GetAttachRequest(debuggableProcess.Id);
		    debugProtocolHost.SendRequestSync(attachRequest);
		    await initializedEventTcs.Task;
		    var setBreakpointsRequest = DebugAdapterProcessHelper.GetSetBreakpointsRequest();
		    var breakpointsResponse = debugProtocolHost.SendRequestSync(setBreakpointsRequest);

		    var configurationDoneRequest = new ConfigurationDoneRequest();
		    debugProtocolHost.SendRequestSync(configurationDoneRequest);
		    // DiagnosticsClient.ResumeRuntime seems to have a different implementation on MacOS - it will throw if the runtime is not paused...
		    if (startSuspended) new DiagnosticsClient(debuggableProcess.Id).ResumeRuntime();
		    await Task.Delay(5000, TestContext.Current.CancellationToken);
	    }
	    finally
	    {
		    process.Kill();
		    debuggableProcess.Kill();
	    }
    }

    [Fact]
    public async Task DotnetDbgCli_StackTraceRequest_Returns()
    {
	    var startSuspended = false;
	    var process = DebugAdapterProcessHelper.GetDebugAdapterProcess();
	    var debuggableProcess = DebuggableProcessHelper.StartDebuggableProcess(startSuspended);
	    try
	    {
		    var initializedEventTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		    var debugProtocolHost = DebugAdapterProcessHelper.GetDebugProtocolHost(process, testOutputHelper, initializedEventTcs);
		    var stoppedEventTcs = new TaskCompletionSource<StoppedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
		    debugProtocolHost.RegisterEventType<StoppedEvent>(@event => stoppedEventTcs.TrySetResult(@event));
			debugProtocolHost.Run();
		    var initializeRequest = DebugAdapterProcessHelper.GetInitializeRequest();
		    debugProtocolHost.SendRequestSync(initializeRequest);
		    var attachRequest = DebugAdapterProcessHelper.GetAttachRequest(debuggableProcess.Id);
		    debugProtocolHost.SendRequestSync(attachRequest);
		    await initializedEventTcs.Task;
		    var setBreakpointsRequest = DebugAdapterProcessHelper.GetSetBreakpointsRequest();
		    var breakpointsResponse = debugProtocolHost.SendRequestSync(setBreakpointsRequest);

		    var configurationDoneRequest = new ConfigurationDoneRequest();
		    debugProtocolHost.SendRequestSync(configurationDoneRequest);
		    // DiagnosticsClient.ResumeRuntime seems to have a different implementation on MacOS - it will throw if the runtime is not paused...
		    if (startSuspended) new DiagnosticsClient(debuggableProcess.Id).ResumeRuntime();

		    var stoppedEvent = await stoppedEventTcs.Task;
		    ;
		    var stackTraceRequest = new StackTraceRequest { ThreadId = stoppedEvent.ThreadId!.Value, StartFrame = 0, Levels = 1 };
		    var stackTraceResponse = debugProtocolHost.SendRequestSync(stackTraceRequest);
		    await Verify(stackTraceResponse);
	    }
	    finally
	    {
		    process.Kill();
		    debuggableProcess.Kill();
	    }
    }

    [Fact]
    public async Task DotnetDbgCli_ScopesRequest_Returns()
    {
	    var startSuspended = false;
	    var process = DebugAdapterProcessHelper.GetDebugAdapterProcess();
	    var debuggableProcess = DebuggableProcessHelper.StartDebuggableProcess(startSuspended);
	    try
	    {
		    var initializedEventTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		    var debugProtocolHost = DebugAdapterProcessHelper.GetDebugProtocolHost(process, testOutputHelper, initializedEventTcs);
		    var stoppedEventTcs = new TaskCompletionSource<StoppedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
		    debugProtocolHost.RegisterEventType<StoppedEvent>(@event => stoppedEventTcs.TrySetResult(@event));
			debugProtocolHost.Run();
		    var initializeRequest = DebugAdapterProcessHelper.GetInitializeRequest();
		    debugProtocolHost.SendRequestSync(initializeRequest);
		    var attachRequest = DebugAdapterProcessHelper.GetAttachRequest(debuggableProcess.Id);
		    debugProtocolHost.SendRequestSync(attachRequest);
		    await initializedEventTcs.Task;
		    var setBreakpointsRequest = DebugAdapterProcessHelper.GetSetBreakpointsRequest();
		    var breakpointsResponse = debugProtocolHost.SendRequestSync(setBreakpointsRequest);

		    var configurationDoneRequest = new ConfigurationDoneRequest();
		    debugProtocolHost.SendRequestSync(configurationDoneRequest);
		    // DiagnosticsClient.ResumeRuntime seems to have a different implementation on MacOS - it will throw if the runtime is not paused...
		    if (startSuspended) new DiagnosticsClient(debuggableProcess.Id).ResumeRuntime();

		    var stoppedEvent = await stoppedEventTcs.Task;

		    var stackTraceRequest = new StackTraceRequest { ThreadId = stoppedEvent.ThreadId!.Value, StartFrame = 0, Levels = 1 };
		    var stackTraceResponse = debugProtocolHost.SendRequestSync(stackTraceRequest);

		    var scopesRequest = new ScopesRequest { FrameId = stackTraceResponse.StackFrames!.First().Id };
		    var scopesResponse = debugProtocolHost.SendRequestSync(scopesRequest);
		    scopesResponse.Scopes.Should().HaveCount(1);
		    var scope = scopesResponse.Scopes.Single();

		    List<Variable> expectedVariables =
		    [
		    	new Variable() {Name = "this", Value = "{DebuggableConsoleApp.MyClass}", Type = "DebuggableConsoleApp.MyClass", EvaluateName = "this", VariablesReference = 2, NamedVariables = 2 },
		    	new Variable() {Name = "myParam", Value = "13", Type = "long", EvaluateName = "myParam" },
		    	new Variable() {Name = "myInt", Value = "0", Type = "int", EvaluateName = "myInt" },
		    	new Variable() {Name = "anotherVar", Value = "null", Type = "string", EvaluateName = "anotherVar" },
		    ];

		    var variablesRequest = new VariablesRequest { VariablesReference = scope.VariablesReference };
		    var variablesResponse = debugProtocolHost.SendRequestSync(variablesRequest);
		    var variables = variablesResponse.Variables;
		    await Verify(variablesResponse);
		    variables.Should().HaveCount(4);
		    variables.Should().BeEquivalentTo(expectedVariables);
	    }
	    finally
	    {
		    process.Kill();
		    debuggableProcess.Kill();
	    }
    }

    [Fact]
    public async Task DotnetDbgCli_LocalVariable_Class_Variables_Returns()
    {
	    var startSuspended = false;
	    var process = DebugAdapterProcessHelper.GetDebugAdapterProcess();
	    var debuggableProcess = DebuggableProcessHelper.StartDebuggableProcess(startSuspended);
	    try
	    {
		    var initializedEventTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		    var debugProtocolHost = DebugAdapterProcessHelper.GetDebugProtocolHost(process, testOutputHelper, initializedEventTcs);
		    var stoppedEventTcs = new TaskCompletionSource<StoppedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
		    debugProtocolHost.RegisterEventType<StoppedEvent>(@event => stoppedEventTcs.TrySetResult(@event));
			debugProtocolHost.Run();
		    var initializeRequest = DebugAdapterProcessHelper.GetInitializeRequest();
		    debugProtocolHost.SendRequestSync(initializeRequest);
		    var attachRequest = DebugAdapterProcessHelper.GetAttachRequest(debuggableProcess.Id);
		    debugProtocolHost.SendRequestSync(attachRequest);
		    await initializedEventTcs.Task;
		    var setBreakpointsRequest = DebugAdapterProcessHelper.GetSetBreakpointsRequest();
		    var breakpointsResponse = debugProtocolHost.SendRequestSync(setBreakpointsRequest);

		    var configurationDoneRequest = new ConfigurationDoneRequest();
		    debugProtocolHost.SendRequestSync(configurationDoneRequest);
		    // DiagnosticsClient.ResumeRuntime seems to have a different implementation on MacOS - it will throw if the runtime is not paused...
		    if (startSuspended) new DiagnosticsClient(debuggableProcess.Id).ResumeRuntime();

		    var stoppedEvent = await stoppedEventTcs.Task;

		    var stackTraceRequest = new StackTraceRequest { ThreadId = stoppedEvent.ThreadId!.Value, StartFrame = 0, Levels = 1 };
		    var stackTraceResponse = debugProtocolHost.SendRequestSync(stackTraceRequest);

		    var scopesRequest = new ScopesRequest { FrameId = stackTraceResponse.StackFrames!.First().Id };
		    var scopesResponse = debugProtocolHost.SendRequestSync(scopesRequest);
		    scopesResponse.Scopes.Should().HaveCount(1);
		    var scope = scopesResponse.Scopes.Single();

		    var variablesRequest = new VariablesRequest { VariablesReference = scope.VariablesReference };
		    var variablesResponse = debugProtocolHost.SendRequestSync(variablesRequest);
		    var variables = variablesResponse.Variables;
		    var thisVariable = variables.Single(v => v.Name == "this");
		    var nestedVariablesRequest = new VariablesRequest { VariablesReference = thisVariable.VariablesReference };
		    var nestedVariablesResponse = debugProtocolHost.SendRequestSync(nestedVariablesRequest);
		    var nestedVariables = nestedVariablesResponse.Variables;
		    await Verify(nestedVariables)
		    ;
	    }
	    finally
	    {
		    process.Kill();
		    debuggableProcess.Kill();
	    }
    }

    [Fact]
    public async Task DotnetDbgCli_VariablesRequest_InstanceMethodInClassWithNoMembers_ThisVarHasNoVariablesReference()
    {
	    var startSuspended = false;
	    var process = DebugAdapterProcessHelper.GetDebugAdapterProcess();
	    var debuggableProcess = DebuggableProcessHelper.StartDebuggableProcess(startSuspended);
	    try
	    {
		    var initializedEventTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		    var debugProtocolHost = DebugAdapterProcessHelper.GetDebugProtocolHost(process, testOutputHelper, initializedEventTcs);
		    var stoppedEventTcs = new TaskCompletionSource<StoppedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
		    debugProtocolHost.RegisterEventType<StoppedEvent>(@event => stoppedEventTcs.TrySetResult(@event));
			debugProtocolHost.Run();
		    var initializeRequest = DebugAdapterProcessHelper.GetInitializeRequest();
		    debugProtocolHost.SendRequestSync(initializeRequest);
		    var attachRequest = DebugAdapterProcessHelper.GetAttachRequest(debuggableProcess.Id);
		    debugProtocolHost.SendRequestSync(attachRequest);
		    await initializedEventTcs.Task;
		    var setBreakpointsRequest = DebugAdapterProcessHelper.GetSetBreakpointsRequest(8, @"C:\Users\Matthew\Documents\Git\dotnetdbg\tests\DebuggableConsoleApp\MyClassNoMembers.cs");
		    var breakpointsResponse = debugProtocolHost.SendRequestSync(setBreakpointsRequest);

		    var configurationDoneRequest = new ConfigurationDoneRequest();
		    debugProtocolHost.SendRequestSync(configurationDoneRequest);
		    // DiagnosticsClient.ResumeRuntime seems to have a different implementation on MacOS - it will throw if the runtime is not paused...
		    if (startSuspended) new DiagnosticsClient(debuggableProcess.Id).ResumeRuntime();

		    var stoppedEvent = await stoppedEventTcs.Task;

		    var stackTraceRequest = new StackTraceRequest { ThreadId = stoppedEvent.ThreadId!.Value, StartFrame = 0, Levels = 1 };
		    var stackTraceResponse = debugProtocolHost.SendRequestSync(stackTraceRequest);

		    var scopesRequest = new ScopesRequest { FrameId = stackTraceResponse.StackFrames!.First().Id };
		    var scopesResponse = debugProtocolHost.SendRequestSync(scopesRequest);
		    scopesResponse.Scopes.Should().HaveCount(1);
		    var scope = scopesResponse.Scopes.Single();

		    var variablesRequest = new VariablesRequest { VariablesReference = scope.VariablesReference };
		    var variablesResponse = debugProtocolHost.SendRequestSync(variablesRequest);
		    var variables = variablesResponse.Variables;
		    await Verify(variablesResponse);
		    var implicitThisVariable = variables.Single(v => v.Name == "this");
		    // VariablesReference is non-nullable, and 0 is treated as null - note the Verify output - VariablesReference does not exist
		    implicitThisVariable.VariablesReference.Should().Be(0);
	    }
	    finally
	    {
		    process.Kill();
		    debuggableProcess.Kill();
	    }
    }

    private class TcsContainer
    {
	    public required TaskCompletionSource<StoppedEvent> Tcs { get; set; }
    }
    [Fact]
    public async Task DotnetDbgCli_NextRequest_ReturnsNextLine()
    {
	    var startSuspended = false;
	    var process = DebugAdapterProcessHelper.GetDebugAdapterProcess();
	    var debuggableProcess = DebuggableProcessHelper.StartDebuggableProcess(startSuspended);
	    try
	    {
		    var initializedEventTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		    var debugProtocolHost = DebugAdapterProcessHelper.GetDebugProtocolHost(process, testOutputHelper, initializedEventTcs);
		    var stoppedEventTcs = new TcsContainer { Tcs = new TaskCompletionSource<StoppedEvent>(TaskCreationOptions.RunContinuationsAsynchronously) };
		    debugProtocolHost.RegisterEventType<StoppedEvent>(@event => stoppedEventTcs.Tcs.TrySetResult(@event));
			debugProtocolHost.Run();
		    var initializeRequest = DebugAdapterProcessHelper.GetInitializeRequest();
		    debugProtocolHost.SendRequestSync(initializeRequest);
		    var attachRequest = DebugAdapterProcessHelper.GetAttachRequest(debuggableProcess.Id);
		    debugProtocolHost.SendRequestSync(attachRequest);
		    await initializedEventTcs.Task;
		    var setBreakpointsRequest = DebugAdapterProcessHelper.GetSetBreakpointsRequest();
		    var breakpointsResponse = debugProtocolHost.SendRequestSync(setBreakpointsRequest);

		    var configurationDoneRequest = new ConfigurationDoneRequest();
		    debugProtocolHost.SendRequestSync(configurationDoneRequest);
		    // DiagnosticsClient.ResumeRuntime seems to have a different implementation on MacOS - it will throw if the runtime is not paused...
		    if (startSuspended) new DiagnosticsClient(debuggableProcess.Id).ResumeRuntime();

		    var stoppedEvent = await stoppedEventTcs.Tcs.Task;
		    var stackTraceRequest = new StackTraceRequest { ThreadId = stoppedEvent.ThreadId!.Value, StartFrame = 0, Levels = 1 };
		    var stackTraceResponse = debugProtocolHost.SendRequestSync(stackTraceRequest);
		    var stackFrame = stackTraceResponse.StackFrames!.First();
		    var currentLine = stackFrame.Line;

		    foreach (var i in Enumerable.Range(0, 10))
		    {
			    stoppedEventTcs.Tcs = new TaskCompletionSource<StoppedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

			    var nextRequest = new NextRequest { ThreadId = stoppedEvent.ThreadId!.Value };
			    debugProtocolHost.SendRequestSync(nextRequest);

			    var stoppedEventAfterNext = await stoppedEventTcs.Tcs.Task;
			    var stackTraceResponseAfterNext = debugProtocolHost.SendRequestSync(new StackTraceRequest { ThreadId = stoppedEventAfterNext.ThreadId!.Value, StartFrame = 0, Levels = 1 });
			    var lineAfterNext = stackTraceResponseAfterNext.StackFrames!.First().Line;
			    lineAfterNext.Should().NotBe(0);
			    ;
		    }
		    //lineAfterNext.Should().Be(currentLine + 1);

	    }
	    finally
	    {
		    process.Kill();
		    debuggableProcess.Kill();
	    }
    }

    [Fact]
    public async Task DotnetDbgCli_VariablesRequest_Returns()
    {
	    var startSuspended = false;
	    var process = DebugAdapterProcessHelper.GetDebugAdapterProcess();
	    var debuggableProcess = DebuggableProcessHelper.StartDebuggableProcess(startSuspended);
	    try
	    {
		    var initializedEventTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		    var debugProtocolHost = DebugAdapterProcessHelper.GetDebugProtocolHost(process, testOutputHelper, initializedEventTcs);
		    var stoppedEventTcs = new TcsContainer { Tcs = new TaskCompletionSource<StoppedEvent>(TaskCreationOptions.RunContinuationsAsynchronously) };
		    debugProtocolHost.RegisterEventType<StoppedEvent>(@event => stoppedEventTcs.Tcs.TrySetResult(@event));
			debugProtocolHost.Run();
		    var initializeRequest = DebugAdapterProcessHelper.GetInitializeRequest();
		    debugProtocolHost.SendRequestSync(initializeRequest);
		    var attachRequest = DebugAdapterProcessHelper.GetAttachRequest(debuggableProcess.Id);
		    debugProtocolHost.SendRequestSync(attachRequest);
		    await initializedEventTcs.Task;
		    var setBreakpointsRequest = DebugAdapterProcessHelper.GetSetBreakpointsRequest();
		    var breakpointsResponse = debugProtocolHost.SendRequestSync(setBreakpointsRequest);

		    var configurationDoneRequest = new ConfigurationDoneRequest();
		    debugProtocolHost.SendRequestSync(configurationDoneRequest);
		    // DiagnosticsClient.ResumeRuntime seems to have a different implementation on MacOS - it will throw if the runtime is not paused...
		    if (startSuspended) new DiagnosticsClient(debuggableProcess.Id).ResumeRuntime();

		    var stoppedEvent = await stoppedEventTcs.Tcs.Task;
		    ;
		    var stackTraceRequest = new StackTraceRequest { ThreadId = stoppedEvent.ThreadId!.Value, StartFrame = 0, Levels = 1 };
		    var stackTraceResponse = debugProtocolHost.SendRequestSync(stackTraceRequest);

		    var scopesRequest = new ScopesRequest { FrameId = stackTraceResponse.StackFrames!.First().Id };
		    var scopesResponse = debugProtocolHost.SendRequestSync(scopesRequest);

		    var scope = scopesResponse.Scopes.First();

		    var variablesRequest = new VariablesRequest { VariablesReference = scope.VariablesReference };
		    var variablesResponse = debugProtocolHost.SendRequestSync(variablesRequest);

		    var thisVariable = variablesResponse.Variables.Single(v => v.Name == "this");

		    var nestedVariablesRequest = new VariablesRequest { VariablesReference = thisVariable.VariablesReference };
		    var nestedVariablesResponse = debugProtocolHost.SendRequestSync(nestedVariablesRequest);

		    var variables = nestedVariablesResponse.Variables;
		    var staticMembersPseudoVariable = variables.Single(v => v.Name == "Static members");
		    var staticMembersVariable = debugProtocolHost.SendRequestSync(new VariablesRequest { VariablesReference = staticMembersPseudoVariable.VariablesReference });
		    var list = staticMembersVariable.Variables.Single(s => s.Name == "_staticIntList");
		    var listVariables = debugProtocolHost.SendRequestSync(new VariablesRequest { VariablesReference = list.VariablesReference });
		    ;
		    await Verify(variables);
	    }
	    finally
	    {
		    process.Kill();
		    debuggableProcess.Kill();
	    }
    }

    [Fact]
    public async Task DotnetDbgCli_ScopesRequest_Returns_V2()
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

	    var stoppedEvent = await stoppedEventTcs.Task;
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
		    new() {Name = "enumVar", Value = "SecondValue", Type = "DebuggableConsoleApp.MyEnum", EvaluateName = "enumVar", VariablesReference = 4},
		    new() {Name = "enumWithFlagsVar", Value = "FlagValue1 | FlagValue3", Type = "DebuggableConsoleApp.MyEnumWithFlags", EvaluateName = "enumWithFlagsVar", VariablesReference = 5},
		    new() {Name = "nullableInt", Value = "null", Type = "int?", EvaluateName = "nullableInt" },
		    new() {Name = "nullableIntWithVal", Value = "4", Type = "int?", EvaluateName = "nullableIntWithVal" },
		    new() {Name = "nullableRefType", Value = "null", Type = "DebuggableConsoleApp.MyClass", EvaluateName = "nullableRefType" },
		    new() {Name = "anotherVar", Value = "asdf", Type = "string", EvaluateName = "anotherVar" },
	    ];

	    debugProtocolHost.WithVariablesRequest(scope.VariablesReference, out var variables);

	    variables.Should().HaveCount(9);
	    variables.Should().BeEquivalentTo(expectedVariables);
	    debugProtocolHost.AssertInstanceThisInstanceVariables(variables.Single(s => s.Name == "this").VariablesReference);

	    List<Variable> expectedEnumVariables =
	    [
		    new() {Name = "Static members", Value = "", Type = "", EvaluateName = "Static members", VariablesReference = 10, PresentationHint = new VariablePresentationHint { Kind = VariablePresentationHint.KindValue.Class }},
		    new() {Name = "value__", Value = "1", Type = "int", EvaluateName = "value__" },
	    ];

	    debugProtocolHost.WithVariablesRequest(variables.Single(s => s.Name == "enumVar").VariablesReference, out var enumNestedVariables);
	    enumNestedVariables.Should().BeEquivalentTo(expectedEnumVariables);

	    List<Variable> expectedEnumStaticMemberVariables =
	    [
		    new() { Name = "FirstValue", Value = "0", Type = "int", EvaluateName = "FirstValue" },
		    new() { Name = "SecondValue", Value = "1", Type = "int", EvaluateName = "SecondValue" },
		    new() { Name = "ThirdValue", Value = "2", Type = "int", EvaluateName = "ThirdValue" },
	    ];

	    debugProtocolHost.WithVariablesRequest(enumNestedVariables.Single(s => s.Name == "Static members").VariablesReference, out var enumStaticVariables);
	    enumStaticVariables.Should().BeEquivalentTo(expectedEnumStaticMemberVariables);
	    // TODO: Assert that none of the variable references are the same (other than 0)
    }
}

public static class TestExtensions
{
	public static void AssertInstanceThisInstanceVariables(this DebugProtocolHost debugProtocolHost, int variablesReference)
	{
		List<Variable> expectedVariables =
		[
		    new() { Name = "Static members", Value = "", Type = "", EvaluateName = "Static members", VariablesReference = 6, PresentationHint = new VariablePresentationHint { Kind = VariablePresentationHint.KindValue.Class }},
			new() { Name = "_name", EvaluateName = "_name", Value = "TestName", Type = "string" },
			new() { Name = "ClassProperty", EvaluateName = "ClassProperty", Value = "{DebuggableConsoleApp.MyClass2}", Type = "DebuggableConsoleApp.MyClass2", VariablesReference = 8 },
			new() { Name = "ClassProperty2", EvaluateName = "ClassProperty2", Value = "{DebuggableConsoleApp.MyClass2}", Type = "DebuggableConsoleApp.MyClass2", VariablesReference = 9 },
			new() { Name = "_intList", EvaluateName = "_intList", Value = "{System.Collections.Generic.List<int>}", Type = "System.Collections.Generic.List<int>", VariablesReference = 7 },
		];
		debugProtocolHost.WithVariablesRequest(variablesReference, out var variables);
		variables.Should().BeEquivalentTo(expectedVariables);
	}
}
