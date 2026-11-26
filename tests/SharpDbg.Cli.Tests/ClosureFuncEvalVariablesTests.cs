using AwesomeAssertions;
using SharpDbg.DapProtocol;
using SharpDbg.Cli.Tests.Helpers;

namespace SharpDbg.Cli.Tests;

/// Hoisted locals whose display needs a func-eval (List<T>'s [DebuggerDisplay], a ToString() override) used to neuter
/// the closure/state machine value mid-walk, failing the whole variables request with "Object is in a zombie state"
public class ClosureFuncEvalVariablesTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task SharpDbgCli_HoistedLocalsNeedingFuncEval_VariablesRequest_Succeeds()
	{
		var startSuspended = true;
		var (debugProtocolHost, adapter, p2) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(p2.Id)
			.WaitForInitializedEvent();
		var filePath = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "ClosureFuncEvalClass.cs");
		debugProtocolHost
			.WithBreakpointsRequest([11, 24, 26, 28], filePath)
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		(string, string)[] expected = [("list", "Count = 3"), ("point", "(1, 2)")];

		// In an async method, after an await: the locals are hoisted onto the state machine
		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
		stoppedEvent.ReadStopInfo().line.Should().Be(11);
		GetTopFrameVariables(stoppedEvent.ThreadId!.Value).Select(v => (v.Name, v.Value)).Should().BeEquivalentTo(expected);

		// In the declaring method: the closure is a local
		var stoppedEvent2 = await debugProtocolHost.WithContinueRequest().WaitForStoppedEvent();
		stoppedEvent2.ReadStopInfo().line.Should().Be(28);
		GetTopFrameVariables(stoppedEvent2.ThreadId!.Value).Select(v => (v.Name, v.Value)).Should().BeEquivalentTo([..expected, ("f", "{System.Func<int>}")]);

		// In the lambda body: the lambda's closure is 'this', and the nested lambda's closure is a local
		var stoppedEvent3 = await debugProtocolHost.WithContinueRequest().WaitForStoppedEvent();
		stoppedEvent3.ReadStopInfo().line.Should().Be(26);
		GetTopFrameVariables(stoppedEvent3.ThreadId!.Value).Select(v => (v.Name, v.Value)).Should().BeEquivalentTo([..expected, ("innerList", "Count = 1"), ("g", "{System.Func<int>}")]);

		// In the nested lambda body: its closure is 'this', and links to the enclosing lambda's closure, which is
		// followed after the func-evals for the nested closure's own members
		var stoppedEvent4 = await debugProtocolHost.WithContinueRequest().WaitForStoppedEvent();
		stoppedEvent4.ReadStopInfo().line.Should().Be(24);
		GetTopFrameVariables(stoppedEvent4.ThreadId!.Value).Select(v => (v.Name, v.Value)).Should().BeEquivalentTo([("innerList", "Count = 1"), ..expected]);

		List<Variable> GetTopFrameVariables(int threadId)
		{
			debugProtocolHost
				.WithStackTraceRequest(threadId, out var stackTraceResponse)
				.WithScopesRequest(stackTraceResponse.StackFrames.First().Id, out var scopesResponse)
				.WithVariablesRequest(scopesResponse.Scopes.Single().VariablesReference, out var variables);
			return variables;
		}
	}
}
