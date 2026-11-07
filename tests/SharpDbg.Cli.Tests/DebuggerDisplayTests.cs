using AwesomeAssertions;
using SharpDbg.Cli.Tests.Helpers;

namespace SharpDbg.Cli.Tests;

public class DebuggerDisplayTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task VariablesRequest_DebuggerDisplay_FormatsEachExpressionAsADebuggerValue()
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
		debugProtocolHost
			.WithBreakpointsRequest(24, Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "DebuggerDisplayFormatting.cs"))
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
		var topFrame = debugProtocolHost.GetTopStackFrame(stoppedEvent.ThreadId!.Value);
		debugProtocolHost
			.WithScopesRequest(topFrame.Id, out var scopesResponse)
			.WithVariablesRequest(scopesResponse.Scopes.Single().VariablesReference, out var variables);

		variables.Single(v => v.Name == "debuggerDisplayFormatting").Value.Should().Be(
			"""Bool = true, String = "text", NoQuotes = text, Null = null, NullableInt = null, Enum = Monday, Char = 120 'x', Add = 3, Nested = Value = "nested", Missing = error: The name 'Missing' does not exist in the current context, Recursive = <Evaluation of DebuggerDisplay attribute reached max recursion count>""");
		variables.Single(v => v.Name == "lazy").Value.Should().Be("ThreadSafetyMode = null, IsValueCreated = true, IsValueFaulted = false, Value = 99");
	}
}
