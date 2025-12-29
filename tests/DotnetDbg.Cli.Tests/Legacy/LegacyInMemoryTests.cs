using AwesomeAssertions;
using DotnetDbg.Application;
using DotnetDbg.Cli.Tests.Helpers;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace DotnetDbg.Cli.Tests.Legacy;

public class LegacyInMemoryTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
    public async Task DotnetDbgCli_StackTraceRequest_Returns()
    {
	    var startSuspended = false;
	    var debuggableProcess = DebuggableProcessHelper.StartDebuggableProcess(startSuspended);
	    DebugAdapter? debugAdapter = null;
	    try
	    {
		    var initializedEventTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		    var (input, output, adapter) = InMemoryDebugAdapterHelper.GetAdapterStreams(testOutputHelper);
		    debugAdapter = adapter;

		    var debugProtocolHost = DebugAdapterProcessHelper.GetDebugProtocolHost(input, output, testOutputHelper, initializedEventTcs);
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

		    var scopesRequest = new ScopesRequest { FrameId = stackTraceResponse.StackFrames!.First().Id };
		    var scopesResponse = debugProtocolHost.SendRequestSync(scopesRequest);

		    var scope = scopesResponse.Scopes.First();

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

		    var variablesRequest = new VariablesRequest { VariablesReference = scope.VariablesReference };
		    var variablesResponse = debugProtocolHost.SendRequestSync(variablesRequest);
		    var variables = variablesResponse.Variables;
		    variables.Should().HaveCount(10);
		    variables.Should().BeEquivalentTo(expectedVariables);
	    }
	    finally
	    {
		    debuggableProcess.Kill();
		    debugAdapter?.Protocol.Stop();
	    }
    }

    [Fact]
    public async Task DotnetDbgCli_LocalVariable_Class_Variables_Returns()
    {
	    var startSuspended = false;
	    var debuggableProcess = DebuggableProcessHelper.StartDebuggableProcess(startSuspended);
	    DebugAdapter? debugAdapter = null;
	    try
	    {
		    var initializedEventTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		    var (input, output, adapter) = InMemoryDebugAdapterHelper.GetAdapterStreams(testOutputHelper);
		    debugAdapter = adapter;

		    var debugProtocolHost = DebugAdapterProcessHelper.GetDebugProtocolHost(input, output, testOutputHelper, initializedEventTcs);
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
		    var staticMemberVariables = debugProtocolHost.SendRequestSync(new VariablesRequest(nestedVariables.Single(s => s.Name == "Static members").VariablesReference));
		    var staticClassProperty = staticMemberVariables.Variables.Single(v => v.Name == "StaticClassProperty");
		    staticClassProperty.VariablesReference.Should().NotBe(0);
		    //await Verify(nestedVariables)
		    ;
	    }
	    finally
	    {
		    debuggableProcess.Kill();
		    debugAdapter?.Protocol.Stop();
	    }
    }

    [Fact]
    public async Task DotnetDbgCli_InMem_NextRequest_ReturnsNextLine()
    {
	    var startSuspended = false;
	    var debuggableProcess = DebuggableProcessHelper.StartDebuggableProcess(startSuspended);
	    DebugAdapter? debugAdapter = null;
	    try
	    {
		    var initializedEventTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		    var (input, output, adapter) = InMemoryDebugAdapterHelper.GetAdapterStreams(testOutputHelper);
		    debugAdapter = adapter;

		    var debugProtocolHost = DebugAdapterProcessHelper.GetDebugProtocolHost(input, output, testOutputHelper, initializedEventTcs);
		    var stoppedEventTcs = new TcsContainer { Tcs = new TaskCompletionSource<StoppedEvent>(TaskCreationOptions.RunContinuationsAsynchronously) };
		    debugProtocolHost.RegisterEventType<StoppedEvent>(@event => stoppedEventTcs.Tcs.SetResult(@event));
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
		    var currentLine = stackTraceResponse.StackFrames!.First().Line;

		    foreach (var i in Enumerable.Range(0, 100))
		    {
			    stoppedEventTcs.Tcs = new TaskCompletionSource<StoppedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

			    var nextRequest = new NextRequest { ThreadId = stoppedEvent.ThreadId!.Value };
			    debugProtocolHost.SendRequestSync(nextRequest);

			    var stoppedEventAfterNext = await stoppedEventTcs.Tcs.Task;
			    stoppedEventAfterNext.ThreadId.Should().Be(stoppedEvent.ThreadId);
			    var stackTraceResponseAfterNext = debugProtocolHost.SendRequestSync(new StackTraceRequest { ThreadId = stoppedEventAfterNext.ThreadId!.Value, StartFrame = 0, Levels = 1 });
			    var lineAfterNext = stackTraceResponseAfterNext.StackFrames!.First().Line;
			    lineAfterNext.Should().NotBe(0);
			    ;
		    }
		    //lineAfterNext.Should().Be(currentLine + 1);
	    }
	    finally
	    {
		    debuggableProcess.Kill();
		    debugAdapter?.Protocol.Stop();
	    }
    }

    [Fact]
    public async Task DotnetDbgCli_VariablesRequestForObject_Returns()
    {
	    var startSuspended = false;
	    var debuggableProcess = DebuggableProcessHelper.StartDebuggableProcess(startSuspended);
	    DebugAdapter? debugAdapter = null;
	    try
	    {
		    var initializedEventTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		    var (input, output, adapter) = InMemoryDebugAdapterHelper.GetAdapterStreams(testOutputHelper);
		    debugAdapter = adapter;

		    var debugProtocolHost = DebugAdapterProcessHelper.GetDebugProtocolHost(input, output, testOutputHelper, initializedEventTcs);
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
		    ;
	    }
	    finally
	    {
		    debuggableProcess.Kill();
		    debugAdapter?.Protocol.Stop();
	    }
    }
}
