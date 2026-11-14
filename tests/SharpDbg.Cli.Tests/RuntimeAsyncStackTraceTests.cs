using AwesomeAssertions;
using SharpDbg.Cli.Tests.Helpers;

namespace SharpDbg.Cli.Tests;

public class RuntimeAsyncStackTraceTests(ITestOutputHelper testOutputHelper)
{
	/// <summary>
	/// After its await of Task.Delay suspends, InnerAsync is resumed by the runtime's dispatcher, so its awaiting callers are not on the
	/// physical stack: they come from the dispatcher's continuation chain
	/// </summary>
	[Fact]
	public async Task StackTraceRequest_AppendsSyntheticAsyncCallerFrames()
	{
		var startSuspended = true;
		var (debugProtocolHost, adapter, process) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended, DebuggableProcessHelper.RuntimeAsyncConsoleApp);
		using var _ = adapter;
		using var __ = new ProcessKiller(process);
		using var ___ = debugProtocolHost;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(process.Id, justMyCode: true)
			.WaitForInitializedEvent();
		var filePath = Path.JoinFromGitRoot("tests", "RuntimeAsyncConsoleApp", "AsyncStackTraceClass.cs");
		debugProtocolHost
			.WithBreakpointsRequest([20], filePath)
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(process.Id, startSuspended);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
		debugProtocolHost.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTraceResponse, null);

		stackTraceResponse.StackFrames.First().Name.Should().Be("RuntimeAsyncConsoleApp.dll!RuntimeAsyncConsoleApp.AsyncStackTraceClass.InnerAsync()");
		stackTraceResponse.StackFrames.TakeLast(2).Select(frame => frame.Name).Should().Equal(
			"RuntimeAsyncConsoleApp.dll!RuntimeAsyncConsoleApp.AsyncStackTraceClass.MiddleAsync()",
			"RuntimeAsyncConsoleApp.dll!RuntimeAsyncConsoleApp.AsyncStackTraceClass.TestAsync()");
		stackTraceResponse.StackFrames.TakeLast(2).Select(frame => frame.Line).Should().Equal(13, 7);

		var syntheticMiddleFrame = stackTraceResponse.StackFrames[^2];
		debugProtocolHost.WithScopesRequest(syntheticMiddleFrame.Id, out var scopesResponse);
		debugProtocolHost.WithVariablesRequest(scopesResponse.Scopes.Single().VariablesReference, out var variables);
		variables.Should().Contain(variable => variable.Name == "middleValue" && variable.Value == "17");
	}
}
