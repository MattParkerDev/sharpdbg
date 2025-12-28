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
		    new() {Name = "myIntParam", Value = "6", Type = "int", EvaluateName = "myIntParam" },
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
	    variables.Should().BeEquivalentTo(expectedVariables);
	    debugProtocolHost.AssertStructMemberVariables(variables.Single(s => s.Name == "structVar").VariablesReference);
	    debugProtocolHost.AssertInstanceThisInstanceVariables(variables.Single(s => s.Name == "this").VariablesReference);

	    List<Variable> expectedEnumVariables =
	    [
		    new() {Name = "Static members", Value = "", Type = "", EvaluateName = "Static members", VariablesReference = 18, PresentationHint = new VariablePresentationHint { Kind = VariablePresentationHint.KindValue.Class }},
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

public static class TestExtensions
{
	public static void AssertStructMemberVariables(this DebugProtocolHost debugProtocolHost, int variablesReference)
	{
		List<Variable> expectedVariables =
		[
			new() { Name = "Id", EvaluateName = "Id", Value = "5", Type = "int" },
			new() { Name = "Name", EvaluateName = "Name", Value = "StructName", Type = "string" },
		];
		debugProtocolHost.WithVariablesRequest(variablesReference, out var structMemberVariables);
		structMemberVariables.Should().BeEquivalentTo(expectedVariables);
	}
	public static void AssertInstanceThisInstanceVariables(this DebugProtocolHost debugProtocolHost, int variablesReference)
	{
		List<Variable> expectedVariables =
		[
		    new() { Name = "Static members", Value = "", Type = "", EvaluateName = "Static members", VariablesReference = 7, PresentationHint = new VariablePresentationHint { Kind = VariablePresentationHint.KindValue.Class }},
			new() { Name = "_name", EvaluateName = "_name", Value = "TestName", Type = "string" },
			new() { Name = "ClassProperty", EvaluateName = "ClassProperty", Value = "{DebuggableConsoleApp.MyClass2}", Type = "DebuggableConsoleApp.MyClass2", VariablesReference = 10 },
			new() { Name = "ClassProperty2", EvaluateName = "ClassProperty2", Value = "{DebuggableConsoleApp.MyClass2}", Type = "DebuggableConsoleApp.MyClass2", VariablesReference = 11 },
			new() { Name = "_intList", EvaluateName = "_intList", Value = "Count = {Count}", Type = "System.Collections.Generic.List<int>", VariablesReference = 8 },
			new() { Name = "_intArray", EvaluateName = "_intArray", Value = "int[4]", Type = "int[]", VariablesReference = 9 },
			new() { Name = "_instanceField", EvaluateName = "_instanceField", Value = "5", Type = "int" },
			new() { Name = "IntProperty", EvaluateName = "IntProperty", Value = "10", Type = "int" },
		];
		debugProtocolHost.WithVariablesRequest(variablesReference, out var thisInstanceVariables);
		thisInstanceVariables.Should().BeEquivalentTo(expectedVariables);
		debugProtocolHost.AssertIntArrayVariables(thisInstanceVariables.Single(s => s.Name == "_intArray").VariablesReference);
		debugProtocolHost.AssertInstanceThisStaticVariables(thisInstanceVariables.Single(s => s.Name == "Static members").VariablesReference);
	}

	public static void AssertInstanceThisStaticVariables(this DebugProtocolHost debugProtocolHost, int variablesReference)
	{
		List<Variable> expectedVariables =
		[
			new() { Name = "_counter", EvaluateName = "_counter", Value = "3", Type = "int" },
			new() { Name = "IntStaticProperty", EvaluateName = "IntStaticProperty", Value = "10", Type = "int" },
			new() { Name = "StaticClassProperty", EvaluateName = "StaticClassProperty", Value = "{DebuggableConsoleApp.MyClass2}", Type = "DebuggableConsoleApp.MyClass2", VariablesReference = 17 },
			new() { Name = "_staticClassField", EvaluateName = "_staticClassField", Value = "{DebuggableConsoleApp.MyClass2}", Type = "DebuggableConsoleApp.MyClass2", VariablesReference = 12 },
			new() { Name = "_staticIntList", EvaluateName = "_staticIntList", Value = "Count = {Count}", Type = "System.Collections.Generic.List<int>", VariablesReference = 13 },
			new() { Name = "_fieldDictionary", EvaluateName = "_fieldDictionary", Value = "Count = {Count}", Type = "System.Collections.Generic.Dictionary<DebuggableConsoleApp.MyClass2, DebuggableConsoleApp.MyClass>", VariablesReference = 14 },
			new() { Name = "_utcNow", EvaluateName = "_utcNow", Value = "{System.DateTime}", Type = "System.DateTime", VariablesReference = 15 },
			new() { Name = "_nullableUtcNow", EvaluateName = "_nullableUtcNow", Value = "{System.DateTime}", Type = "System.DateTime?", VariablesReference = 16 },
			new() { Name = "_instanceStaticField", EvaluateName = "_instanceStaticField", Value = "6", Type = "int" },
		];
		debugProtocolHost.WithVariablesRequest(variablesReference, out var instanceThisStaticVariables);
		instanceThisStaticVariables.Should().BeEquivalentTo(expectedVariables);
	}

	public static void AssertIntArrayVariables(this DebugProtocolHost debugProtocolHost, int variablesReference)
	{
		List<Variable> expectedVariables =
		[
			new() { Name = "[0]", EvaluateName = "[0]", Value = "2", Type = "int" },
			new() { Name = "[1]", EvaluateName = "[1]", Value = "3", Type = "int" },
			new() { Name = "[2]", EvaluateName = "[2]", Value = "5", Type = "int" },
			new() { Name = "[3]", EvaluateName = "[3]", Value = "7", Type = "int" },
		];
		debugProtocolHost.WithVariablesRequest(variablesReference, out var intArrayVariables);
		intArrayVariables.Should().BeEquivalentTo(expectedVariables);
	}
}
