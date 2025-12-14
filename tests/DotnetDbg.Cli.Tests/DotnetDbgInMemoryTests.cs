using AwesomeAssertions;
using DotnetDbg.Application;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace DotnetDbg.Cli.Tests;

public class DotnetDbgInMemoryTests(ITestOutputHelper testOutputHelper)
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
			    new Variable() {Name = "this", Value = "{DebuggableConsoleApp.MyClass}", Type = "DebuggableConsoleApp.MyClass", EvaluateName = "this", VariablesReference = 2, NamedVariables = 2 },
			    new Variable() {Name = "myInt", Value = "0", Type = "int", EvaluateName = "myInt" },
			    new Variable() {Name = "anotherVar", Value = "null", Type = "string", EvaluateName = "anotherVar" },
		    	new Variable() {Name = "myParam", Value = "13", Type = "long", EvaluateName = "myParam" },
		    ];

		    var variablesRequest = new VariablesRequest { VariablesReference = scope.VariablesReference };
		    var variablesResponse = debugProtocolHost.SendRequestSync(variablesRequest);
		    var variables = variablesResponse.Variables;
		    variables.Should().HaveCount(4);
		    variables.Should().BeEquivalentTo(expectedVariables);
	    }
	    finally
	    {
		    debuggableProcess.Kill();
		    debugAdapter?.Protocol.Stop();
	    }
    }

	private class TcsContainer
	{
		public required TaskCompletionSource<StoppedEvent> Tcs { get; set; }
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
		    var currentLine = stackTraceResponse.StackFrames!.First().Line;

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
		    debuggableProcess.Kill();
		    debugAdapter?.Protocol.Stop();
	    }
    }
}
