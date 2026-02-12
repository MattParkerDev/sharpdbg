using System.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using SharpDbg.Cli.Tests.Helpers;

namespace SharpDbg.Cli.Tests;

public class TcsContainer
{
	public required TaskCompletionSource<StoppedEvent> Tcs { get; set; }
}

public static partial class TestHelper
{
	public static (DebugProtocolHost, TaskCompletionSource InitializedEventTcs, TcsContainer StoppedEventTcs, OopOrInProcDebugAdapter DebugAdapterProcess, Process DebuggableProcess) GetRunningDebugProtocolHostOop(ITestOutputHelper testOutputHelper, bool startSuspended)
	{
		var process = DebugAdapterProcessHelper.GetDebugAdapterProcess();
		var debuggableProcess = DebuggableProcessHelper.StartDebuggableProcess(startSuspended);
		var initializedEventTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var debugProtocolHost = DebugAdapterProcessHelper.GetDebugProtocolHost(process, testOutputHelper, initializedEventTcs);
		var stoppedEventTcs = new TcsContainer { Tcs = new TaskCompletionSource<StoppedEvent>(TaskCreationOptions.RunContinuationsAsynchronously) };
		debugProtocolHost.RegisterEventType<StoppedEvent>(@event => stoppedEventTcs.Tcs.TrySetResult(@event));
		debugProtocolHost.Run();
		return (debugProtocolHost, initializedEventTcs, stoppedEventTcs, new OopOrInProcDebugAdapter(process), debuggableProcess);
	}

	public static (DebugProtocolHost, TaskCompletionSource InitializedEventTcs, TcsContainer, OopOrInProcDebugAdapter DebugAdapter, Process DebuggableProcess) GetRunningDebugProtocolHostInProc(ITestOutputHelper testOutputHelper, bool startSuspended)
	{
		var (input, output, adapter) = InMemoryDebugAdapterHelper.GetAdapterStreams(testOutputHelper);
		var debuggableProcess = DebuggableProcessHelper.StartDebuggableProcess(startSuspended);
		var initializedEventTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var debugProtocolHost = DebugAdapterProcessHelper.GetDebugProtocolHost(input, output, testOutputHelper, initializedEventTcs);
		var stoppedEventTcs = new TcsContainer { Tcs = new TaskCompletionSource<StoppedEvent>(TaskCreationOptions.RunContinuationsAsynchronously) };
		debugProtocolHost.RegisterEventType<StoppedEvent>(@event => stoppedEventTcs.Tcs.TrySetResult(@event));
		debugProtocolHost.Run();
		return (debugProtocolHost, initializedEventTcs, stoppedEventTcs, new OopOrInProcDebugAdapter(adapter), debuggableProcess);
	}

	public static DebugProtocolHost WithInitializeRequest(this DebugProtocolHost debugProtocolHost)
	{
		var initializeRequest = DebugAdapterProcessHelper.GetInitializeRequest();
		debugProtocolHost.SendRequestSync(initializeRequest);
		return debugProtocolHost;
	}

	public static DebugProtocolHost WithAttachRequest(this DebugProtocolHost debugProtocolHost, int debuggableProcessId)
	{
		var attachRequest = DebugAdapterProcessHelper.GetAttachRequest(debuggableProcessId);
		debugProtocolHost.SendRequestSync(attachRequest);
		return debugProtocolHost;
	}
	public static async Task<DebugProtocolHost> WaitForInitializedEvent(this DebugProtocolHost debugProtocolHost, TaskCompletionSource initializedEventTcs)
	{
		await initializedEventTcs.Task.WaitAsync(TestContext.Current.CancellationToken);
		return debugProtocolHost;
	}
	public static async Task<StoppedEvent> WaitForStoppedEvent(this DebugProtocolHost debugProtocolHost, TcsContainer stoppedEventTcsContainer)
	{
		var stoppedEvent = await stoppedEventTcsContainer.Tcs.Task.WaitAsync(TestContext.Current.CancellationToken);
		stoppedEventTcsContainer.Tcs = new TaskCompletionSource<StoppedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
		FillingMissingNetCoreDbgStopInfo(debugProtocolHost, stoppedEvent);
		return stoppedEvent;
	}

	public static DebugProtocolHost WithBreakpointsRequest(this DebugProtocolHost debugProtocolHost, int[] lines, string filePath)
	{
		var setBreakpointsRequest = DebugAdapterProcessHelper.GetSetBreakpointsRequest(lines, filePath);
		if (File.Exists(setBreakpointsRequest.Source.Path) is false) throw new FileNotFoundException("Source file for breakpoint not found", setBreakpointsRequest.Source.Path);
		debugProtocolHost.SendRequestSync(setBreakpointsRequest);
		return debugProtocolHost;
	}
	public static DebugProtocolHost WithBreakpointsRequest(this DebugProtocolHost debugProtocolHost, int? line = null, string? filePath = null)
	{
		var setBreakpointsRequest = DebugAdapterProcessHelper.GetSetBreakpointsRequest(line, filePath);
		if (File.Exists(setBreakpointsRequest.Source.Path) is false) throw new FileNotFoundException("Source file for breakpoint not found", setBreakpointsRequest.Source.Path);
		debugProtocolHost.SendRequestSync(setBreakpointsRequest);
		return debugProtocolHost;
	}

	public static DebugProtocolHost WithConditionalBreakpointsRequest(this DebugProtocolHost debugProtocolHost, int line, string? condition = null, string? hitCondition = null, string? filePath = null)
	{
		var setBreakpointsRequest = DebugAdapterProcessHelper.GetSetBreakpointsRequest(line, filePath, condition, hitCondition);
		if (File.Exists(setBreakpointsRequest.Source.Path) is false) throw new FileNotFoundException("Source file for breakpoint not found", setBreakpointsRequest.Source.Path);
		debugProtocolHost.SendRequestSync(setBreakpointsRequest);
		return debugProtocolHost;
	}

	public static DebugProtocolHost WithClearBreakpointsRequest(this DebugProtocolHost debugProtocolHost, string filePath)
	{
		var setBreakpointsRequest = new SetBreakpointsRequest
		{
			Source = new Source { Path = filePath },
			Breakpoints = []
		};
		if (File.Exists(setBreakpointsRequest.Source.Path) is false) throw new FileNotFoundException("Source file for breakpoint not found", setBreakpointsRequest.Source.Path);
		debugProtocolHost.SendRequestSync(setBreakpointsRequest);
		return debugProtocolHost;
	}

	public static DebugProtocolHost WithConfigurationDoneRequest(this DebugProtocolHost debugProtocolHost)
	{
		var configurationDoneRequest = new ConfigurationDoneRequest();
		debugProtocolHost.SendRequestSync(configurationDoneRequest);
		return debugProtocolHost;
	}

	public static DebugProtocolHost WithOptionalResumeRuntime(this DebugProtocolHost debugProtocolHost, int processId, bool startSuspended)
	{
	    // DiagnosticsClient.ResumeRuntime seems to have a different implementation on MacOS - it will throw if the runtime is not paused...
	    if (startSuspended) new DiagnosticsClient(processId).ResumeRuntime();
		return debugProtocolHost;
	}

	public static DebugProtocolHost WithStackTraceRequest(this DebugProtocolHost debugProtocolHost, int threadId, out StackTraceResponse stackTraceResponse)
	{
		var stackTraceRequest = new StackTraceRequest { ThreadId = threadId, StartFrame = 0, Levels = 1 };
		stackTraceResponse = debugProtocolHost.SendRequestSync(stackTraceRequest);
		return debugProtocolHost;
	}

	public static DebugProtocolHost WithScopesRequest(this DebugProtocolHost debugProtocolHost, int frameId, out ScopesResponse scopesResponse)
	{
		var scopesRequest = new ScopesRequest { FrameId = frameId };
		scopesResponse = debugProtocolHost.SendRequestSync(scopesRequest);
		return debugProtocolHost;
	}

	public static DebugProtocolHost WithVariablesRequest(this DebugProtocolHost debugProtocolHost, int variablesReference, out List<Variable> variablesResponse)
	{
		var variablesRequest = new VariablesRequest { VariablesReference = variablesReference };
		variablesResponse = debugProtocolHost.SendRequestSync(variablesRequest).Variables;
		return debugProtocolHost;
	}

	public static DebugProtocolHost WithContinueRequest(this DebugProtocolHost debugProtocolHost)
	{
		var continueRequest = new ContinueRequest();
		debugProtocolHost.SendRequestSync(continueRequest);
		return debugProtocolHost;
	}

	public static DebugProtocolHost WithStepInRequest(this DebugProtocolHost debugProtocolHost, int threadId)
	{
		var stepInRequest = new StepInRequest(threadId);
		debugProtocolHost.SendRequestSync(stepInRequest);
		return debugProtocolHost;
	}

	public static DebugProtocolHost WithStepOutRequest(this DebugProtocolHost debugProtocolHost, int threadId)
	{
		var stepOutRequest = new StepOutRequest(threadId);
		debugProtocolHost.SendRequestSync(stepOutRequest);
		return debugProtocolHost;
	}

	public static DebugProtocolHost WithStepOverRequest(this DebugProtocolHost debugProtocolHost, int threadId)
	{
		var nextRequest = new NextRequest(threadId);
		debugProtocolHost.SendRequestSync(nextRequest);
		return debugProtocolHost;
	}

	public static DebugProtocolHost WithEvaluateRequest(this DebugProtocolHost debugProtocolHost, int frameId, string expression, out EvaluateResponse evaluateResponse)
	{
		var evaluateRequest = new EvaluateRequest { Expression = expression, FrameId = frameId, Context = EvaluateArguments.ContextValue.Repl };
		evaluateResponse = debugProtocolHost.SendRequestSync(evaluateRequest);
		return debugProtocolHost;
	}
}
