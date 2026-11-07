using AwesomeAssertions;
using SharpDbg.Cli.Tests.Helpers;

namespace SharpDbg.Cli.Tests;

// Corners where the CIL interpreter's result must equal the runtime's
public class EvalSemanticsTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task EvaluateRequest_InterpreterCorners_MatchRuntimeSemantics()
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
			.WithBreakpointsRequest(18, Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "EvaluationSemanticsClass.cs"))
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
		var frameId = debugProtocolHost.GetTopStackFrame(stoppedEvent.ThreadId!.Value).Id;

		(string Expression, string Expected)[] cases =
		[
			("nan >= 0.0 ? 1 : 2", "2"),
			("nan <= 0.0 ? 1 : 2", "2"),
			("nan > 0.0 ? 1 : 2", "2"),
			("nan < 0.0 ? 1 : 2", "2"),
			("nan == nan ? 1 : 2", "2"),
			("nan != nan ? 1 : 2", "1"),
			("nan >= 0.0 || nan <= 0.0", "false"),
			("!(nan >= 0.0)", "true"),
			("count >= -7 ? 1 : 2", "1"),
			("count <= -8 ? 1 : 2", "2"),
			("maybe is int", "true"),
			("(object)maybe is int", "true"),
			("(int)boxedInt", "42"),
			("checked((nint)big)", "5"),
			("Options.C.ToString()", "\"C\""),
			("string.Empty.Length", "0"),
			("map.Keys.Count", "2"),
			("matrix[1, 0]", "3"),
			("derived.Value", "9"),
			("\"\".Length", "0"),
			("(int?)boxedInt", "42"),
			("((int?)null).HasValue", "false"),
			("matrix[0, 1] + matrix[1, 1]", "6"),
			("(bool)boxedFalse ? \"yes\" : \"no\"", "\"no\""),
			("!(bool)boxedFalse ? \"yes\" : \"no\"", "\"yes\""),
		];

		var failures = new List<string>();
		foreach (var (expression, expected) in cases)
		{
			string actual;
			try
			{
				var response = debugProtocolHost.Evaluate(frameId, expression);
				actual = response.Result;
			}
			catch (Exception ex)
			{
				actual = $"<error: {ex.Message}>";
			}
			if (actual != expected) failures.Add($"{expression} => {actual} (expected {expected})");
		}
		failures.Should().BeEmpty(string.Join(Environment.NewLine, failures));

		// A getter declared on a generic base runs with the base's type arguments, not the derived type's (none)
		var derivedResponse = debugProtocolHost.Evaluate(frameId, "derived");
		debugProtocolHost.WithVariablesRequest(derivedResponse.VariablesReference, out var derivedMembers);
		derivedMembers.Single(member => member.Name == "Value").Value.Should().Be("9");
	}

	// A 'ref' parameter is an address: reading it is ldarg + ldind, assigning it is ldarg + stind
	[Fact]
	public async Task EvaluateRequest_RefParameter_ReadsAndWritesThroughTheReference()
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
			.WithBreakpointsRequest(26, Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "EvaluationSemanticsClass.cs"))
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
		var frameId = debugProtocolHost.GetTopStackFrame(stoppedEvent.ThreadId!.Value).Id;

		debugProtocolHost.Evaluate(frameId, "value + 1").Result.Should().Be("12");
		debugProtocolHost.Evaluate(frameId, "value = 20").Result.Should().Be("20");
		debugProtocolHost.Evaluate(frameId, "value").Result.Should().Be("20");
	}
}
