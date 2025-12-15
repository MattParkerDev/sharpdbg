using System.Threading.Channels;
using AwesomeAssertions;
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
		    await Verify(variables);
	    }
	    finally
	    {
		    process.Kill();
		    debuggableProcess.Kill();
	    }
    }
}
