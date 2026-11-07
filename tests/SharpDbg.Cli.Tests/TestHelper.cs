using System.Diagnostics;
using AwesomeAssertions;
using Microsoft.Diagnostics.NETCore.Client;
using SharpDbg.Cli.Tests.Helpers;
using SharpDbg.Infrastructure.Debugger;
using SharpDbg.InMemory;
using SharpDbg.DapProtocol;
using StackFrame = SharpDbg.DapProtocol.StackFrame;

namespace SharpDbg.Cli.Tests;

public enum DebugAdapterMode
{
	InProc,
	OutOfProc
}

public static partial class TestHelper
{
	public const VariableReferenceComparison ReferenceComparison = VariableReferenceComparison.Exact;
	public static (DebugProtocolHost, IDisposable DebugAdapterDisposable, Process DebuggableProcess) GetRunningDebugProtocolHost(ITestOutputHelper testOutputHelper, bool startSuspended)
	{
		var debugAdapterMode = DebugAdapterMode.InProc;
		var result = debugAdapterMode switch
		{
			DebugAdapterMode.InProc => GetRunningDebugProtocolHostInProc(testOutputHelper, startSuspended),
			DebugAdapterMode.OutOfProc => GetRunningDebugProtocolHostOop(testOutputHelper, startSuspended),
			_ => throw new NotImplementedException($"Debug adapter mode {debugAdapterMode} is not implemented.")
		};
		return result;
	}

	public static (DebugProtocolHost, IDisposable DebugAdapterDisposable, Process DebuggableProcess) GetRunningDebugProtocolHostOop(ITestOutputHelper testOutputHelper, bool startSuspended)
	{
		var process = DebugAdapterProcessHelper.GetDebugAdapterProcess();
		return GetRunningDebugProtocolHostCore(testOutputHelper, startSuspended, process.StandardInput.BaseStream, process.StandardOutput.BaseStream, new ProcessKiller(process));
	}

	public static (DebugProtocolHost, IDisposable DebugAdapterDisposable, Process DebuggableProcess) GetRunningDebugProtocolHostInProc(ITestOutputHelper testOutputHelper, bool startSuspended)
	{
		var (input, output, debugAdapterDisposable) = SharpDbgInMemory.NewDebugAdapterStreams(Log);
		return GetRunningDebugProtocolHostCore(testOutputHelper, startSuspended, input, output, debugAdapterDisposable);
		void Log(string message)
		{
			testOutputHelper.WriteLine($"Log [SharpDbg]: {message}");
		}
	}

	public static (DebugProtocolHost Host, IDisposable Adapter) GetLaunchDebugProtocolHostInProc(ITestOutputHelper testOutputHelper)
	{
		var (input, output, adapter) = SharpDbgInMemory.NewDebugAdapterStreams(message => testOutputHelper.WriteLine($"Log [SharpDbg]: {message}"));
		var host = DebugAdapterProcessHelper.GetDebugProtocolHost(input, output, testOutputHelper, terminateDebuggeeOnDispose: true);
		host.Run();
		return (host, adapter);
	}

	private static (DebugProtocolHost, IDisposable DebugAdapterDisposable, Process DebuggableProcess) GetRunningDebugProtocolHostCore(ITestOutputHelper testOutputHelper, bool startSuspended, Stream input, Stream output, IDisposable debugAdapterDisposable)
	{
		var debuggableProcess = DebuggableProcessHelper.StartDebuggableProcess(startSuspended);
		var debugProtocolHost = DebugAdapterProcessHelper.GetDebugProtocolHost(input, output, testOutputHelper);
		debugProtocolHost.Run();
		return (debugProtocolHost, debugAdapterDisposable, debuggableProcess);
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

	public static DebugProtocolHost WithLaunchRequest(this DebugProtocolHost debugProtocolHost, string program, bool stopAtEntry, bool justMyCode)
	{
		debugProtocolHost.SendRequestSync(DebugAdapterProcessHelper.GetLaunchRequest(program, stopAtEntry, justMyCode));
		return debugProtocolHost;
	}
	public static async Task<DebugProtocolHost> WaitForInitializedEvent(this DebugProtocolHost debugProtocolHost)
	{
		await debugProtocolHost.WaitForEvent<InitializedEvent>();
		return debugProtocolHost;
	}
	public static async Task<StoppedEvent> WaitForStoppedEvent(this DebugProtocolHost debugProtocolHost)
	{
		var stoppedEvent = await debugProtocolHost.WaitForEvent<StoppedEvent>();
		FillingMissingNetCoreDbgStopInfo(debugProtocolHost, stoppedEvent);
		return stoppedEvent;
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
		var setBreakpointsRequest = new SetBreakpointsArguments
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
		debugProtocolHost.SendRequestSync(new ConfigurationDoneArguments());
		return debugProtocolHost;
	}

	public static DebugProtocolHost WithOptionalResumeRuntime(this DebugProtocolHost debugProtocolHost, int processId, bool startSuspended)
	{
		// DiagnosticsClient.ResumeRuntime seems to have a different implementation on MacOS - it will throw if the runtime is not paused...
		if (startSuspended) new DiagnosticsClient(processId).ResumeRuntime();
		return debugProtocolHost;
	}

	public static StackFrame GetTopStackFrame(this DebugProtocolHost debugProtocolHost, int threadId)
	{
		var stackTraceResponse = debugProtocolHost.SendRequestSync(new StackTraceArguments { ThreadId = threadId, StartFrame = 0, Levels = 1 });
		var topFrame = stackTraceResponse.StackFrames.Single();
		return topFrame;
	}

	public static DebugProtocolHost WithStackTraceRequest(this DebugProtocolHost debugProtocolHost, int threadId, out StackTraceResponse stackTraceResponse, int? levels = 1)
	{
		stackTraceResponse = debugProtocolHost.SendRequestSync(new StackTraceArguments { ThreadId = threadId, StartFrame = 0, Levels = levels });
		return debugProtocolHost;
	}

	public static DebugProtocolHost WithScopesRequest(this DebugProtocolHost debugProtocolHost, int frameId, out ScopesResponse scopesResponse)
	{
		scopesResponse = debugProtocolHost.SendRequestSync(new ScopesArguments { FrameId = frameId });
		return debugProtocolHost;
	}

	public static DebugProtocolHost WithVariablesRequest(this DebugProtocolHost debugProtocolHost, int variablesReference, out List<Variable> variablesResponse)
	{
		variablesResponse = debugProtocolHost.SendRequestSync(new VariablesArguments { VariablesReference = variablesReference }).Variables.ToList();
		return debugProtocolHost;
	}

	public static DebugProtocolHost WithContinueRequest(this DebugProtocolHost debugProtocolHost)
	{
		debugProtocolHost.SendRequestSync(new ContinueArguments());
		return debugProtocolHost;
	}

	public static DebugProtocolHost WithStepInRequest(this DebugProtocolHost debugProtocolHost, int threadId)
	{
		debugProtocolHost.SendRequestSync(new StepInArguments { ThreadId = threadId });
		return debugProtocolHost;
	}

	public static DebugProtocolHost WithStepOutRequest(this DebugProtocolHost debugProtocolHost, int threadId)
	{
		debugProtocolHost.SendRequestSync(new StepOutArguments { ThreadId = threadId });
		return debugProtocolHost;
	}

	public static DebugProtocolHost WithStepOverRequest(this DebugProtocolHost debugProtocolHost, int threadId)
	{
		debugProtocolHost.SendRequestSync(new NextArguments { ThreadId = threadId });
		return debugProtocolHost;
	}

	public static DebugProtocolHost WithEvaluateRequest(this DebugProtocolHost debugProtocolHost, int frameId, string expression, out EvaluateResponse evaluateResponse)
	{
		var evaluateRequest = new EvaluateArguments { Expression = expression, FrameId = frameId, Context = EvaluateArguments.ContextValue.Repl };
		evaluateResponse = debugProtocolHost.SendRequestSync(evaluateRequest);
		return debugProtocolHost;
	}
}
