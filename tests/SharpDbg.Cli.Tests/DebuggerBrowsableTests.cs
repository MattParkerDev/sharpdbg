using AwesomeAssertions;
using SharpDbg.Cli.Tests.Helpers;

namespace SharpDbg.Cli.Tests;

public class DebuggerBrowsableTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task VariablesRequest_DebuggerBrowsableRootHidden_ListsTheMembersChildrenInPlaceOfTheMember()
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
			.WithBreakpointsRequest(23, Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "DebuggerBrowsableRootHidden.cs"))
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
		var topFrame = debugProtocolHost.GetTopStackFrame(stoppedEvent.ThreadId!.Value);
		debugProtocolHost
			.WithScopesRequest(topFrame.Id, out var scopesResponse)
			.WithVariablesRequest(scopesResponse.Scopes.Single().VariablesReference, out var variables);

		debugProtocolHost.WithVariablesRequest(variables.Single(v => v.Name == "rootHidden").VariablesReference, out var rootHiddenMembers);
		// Field and Property are an object's fields, ListField is its proxy's members, and the null and primitive fields have no children
		rootHiddenMembers.Select(v => v.Name).Should().Equal("A", "B", "[0]", "[1]", "Raw View", "Other", "A", "B");

		// A member that lifts itself stops after MaxRootHiddenDepth levels, and is then listed as itself
		debugProtocolHost.WithVariablesRequest(variables.Single(v => v.Name == "rootHiddenCycle").VariablesReference, out var rootHiddenCycleMembers);
		rootHiddenCycleMembers.Select(v => v.Name).Should().Equal([.. Enumerable.Repeat("Other", 11), "Self"]);
	}
}
