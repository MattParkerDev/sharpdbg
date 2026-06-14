using System.Diagnostics;
using AwesomeAssertions;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using SharpDbg.Cli.Tests.Helpers;
using SharpDbg.Infrastructure.Debugger;
using SharpDbg.InMemory;

namespace SharpDbg.Cli.Tests;

public class TcsContainer
{
	public required TaskCompletionSource<DebugEvent> Tcs { get; set; }
}

public static partial class TestHelper
{
	public static (DisposableDebugProtocolHost, TaskCompletionSource InitializedEventTcs, TcsContainer debugEventTcs, IDisposable DebugAdapterDisposable, Process DebuggableProcess) GetRunningDebugProtocolHostOop(ITestOutputHelper testOutputHelper, bool startSuspended)
	{
		var process = DebugAdapterProcessHelper.GetDebugAdapterProcess();
		return GetRunningDebugProtocolHostCore(testOutputHelper, startSuspended, process.StandardInput.BaseStream, process.StandardOutput.BaseStream, new ProcessKiller(process));
	}

	public static (DisposableDebugProtocolHost, TaskCompletionSource InitializedEventTcs, TcsContainer debugEventTcs, IDisposable DebugAdapterDisposable, Process DebuggableProcess) GetRunningDebugProtocolHostInProc(ITestOutputHelper testOutputHelper, bool startSuspended)
	{
		var (input, output, debugAdapterDisposable) = SharpDbgInMemory.NewDebugAdapterStreams(Log);
		return GetRunningDebugProtocolHostCore(testOutputHelper, startSuspended, input, output, debugAdapterDisposable);
		void Log(string message)
		{
			testOutputHelper.WriteLine($"Log [SharpDbg]: {message}");
		}
	}

	private static (DisposableDebugProtocolHost, TaskCompletionSource InitializedEventTcs, TcsContainer debugEventTcs, IDisposable DebugAdapterDisposable, Process DebuggableProcess) GetRunningDebugProtocolHostCore(ITestOutputHelper testOutputHelper, bool startSuspended, Stream input, Stream output, IDisposable debugAdapterDisposable)
	{
		var debuggableProcess = DebuggableProcessHelper.StartDebuggableProcess(startSuspended);
		var initializedEventTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var debugProtocolHost = DebugAdapterProcessHelper.GetDebugProtocolHost(input, output, testOutputHelper, initializedEventTcs);
		var debugEventTcs = new TcsContainer { Tcs = new TaskCompletionSource<DebugEvent>(TaskCreationOptions.RunContinuationsAsynchronously) };
		debugProtocolHost.RegisterEventType<StoppedEvent>(@event => debugEventTcs.Tcs.TrySetResult(@event));
		debugProtocolHost.RegisterEventType<BreakpointEvent>(@event => debugEventTcs.Tcs.TrySetResult(@event));
		debugProtocolHost.Run();
		return (debugProtocolHost, initializedEventTcs, debugEventTcs, debugAdapterDisposable, debuggableProcess);
	}

	public static DebugProtocolHost WithInitializeRequest(this DebugProtocolHost debugProtocolHost)
	{
		var initializeRequest = DebugAdapterProcessHelper.GetInitializeRequest();
		debugProtocolHost.SendRequestSync(initializeRequest);
		return debugProtocolHost;
	}

	public static DebugProtocolHost WithAttachRequest(this DebugProtocolHost debugProtocolHost, int debuggableProcessId, bool justMyCode = true)
	{
		var attachRequest = DebugAdapterProcessHelper.GetAttachRequest(debuggableProcessId, justMyCode);
		debugProtocolHost.SendRequestSync(attachRequest);
		return debugProtocolHost;
	}
	public static async Task<DebugProtocolHost> WaitForInitializedEvent(this DebugProtocolHost debugProtocolHost, TaskCompletionSource initializedEventTcs)
	{
		await initializedEventTcs.Task.WaitAsync(TestContext.Current.CancellationToken);
		return debugProtocolHost;
	}
	public static async Task<StoppedEvent> WaitForStoppedEvent(this DebugProtocolHost debugProtocolHost, TcsContainer eventTcsContainer)
	{
		var stoppedEvent = await debugProtocolHost.WaitForEvent<StoppedEvent>(eventTcsContainer);
		FillingMissingNetCoreDbgStopInfo(debugProtocolHost, stoppedEvent);
		return stoppedEvent;
	}

	public static async Task<T> WaitForEvent<T>(this DebugProtocolHost host, TcsContainer tcsContainer, Predicate<T>? condition = null) where T : DebugEvent
	{
		while (true)
		{
			var e = await tcsContainer.Tcs.Task.WaitAsync(TestContext.Current.CancellationToken);
			tcsContainer.Tcs = new TaskCompletionSource<DebugEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
			if (e is T correctEventType)
			{
				if (condition?.Invoke(correctEventType) is false) continue;
				return correctEventType;
			}
		}
	}

	public static DebugProtocolHost WithBreakpointsRequest(this DebugProtocolHost debugProtocolHost, string filePath, List<SharpDbgBreakpointRequest> breakpointRequests) => debugProtocolHost.WithBreakpointsRequest(filePath, breakpointRequests, out _);
	public static DebugProtocolHost WithBreakpointsRequest(this DebugProtocolHost debugProtocolHost, string filePath, List<SharpDbgBreakpointRequest> breakpointRequests, out SetBreakpointsResponse setBreakpointsResponse)
	{
		var setBreakpointsRequest = DebugAdapterProcessHelper.GetSetBreakpointsRequest(breakpointRequests, filePath);
		if (File.Exists(setBreakpointsRequest.Source.Path) is false) throw new FileNotFoundException("Source file for breakpoint not found", setBreakpointsRequest.Source.Path);
		setBreakpointsResponse = debugProtocolHost.SendRequestSync(setBreakpointsRequest);
		setBreakpointsResponse.Breakpoints.Count.Should().Be(breakpointRequests.Count);
		return debugProtocolHost;
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
