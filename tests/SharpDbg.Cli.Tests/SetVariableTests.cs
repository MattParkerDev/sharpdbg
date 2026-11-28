using AwesomeAssertions;
using AwesomeAssertions.Execution;
using SharpDbg.Cli.Tests.Helpers;
using SharpDbg.DapProtocol;

namespace SharpDbg.Cli.Tests;

public class SetVariableTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task SetVariableRequest_Locals_AreAssignedAndReadBack()
	{
		var startSuspended = true;
		var (debugProtocolHost, adapter, p2) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended);
		using var assertionScope = new AssertionScope();
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(p2.Id)
			.WaitForInitializedEvent();
		debugProtocolHost
			.WithBreakpointsRequest()
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
		debugProtocolHost
			.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTraceResponse)
			.WithScopesRequest(stackTraceResponse.StackFrames!.First().Id, out var scopesResponse);
		var frameId = stackTraceResponse.StackFrames!.First().Id;
		var scopeReference = scopesResponse.Scopes.Single().VariablesReference;
		// A container's variables must have been listed before one of them can be set
		debugProtocolHost.WithVariablesRequest(scopeReference, out var locals);

		// The responses describe the variable's new value, as the specification asks
		var myInt = debugProtocolHost.SetVariable(scopeReference, "myInt", "42");
		myInt.Value.Should().Be("42");
		myInt.Type.Should().Be("int");
		(myInt.VariablesReference ?? 0).Should().Be(0);
		debugProtocolHost.Evaluate(frameId, "myInt").Result.Should().Be("42");

		debugProtocolHost.SetVariable(scopeReference, "myInt", "myIntParam * 2 + 1").Value.Should().Be("13");
		debugProtocolHost.Evaluate(frameId, "myInt").Result.Should().Be("13");

		var myParam = debugProtocolHost.SetVariable(scopeReference, "myParam", "100");
		myParam.Value.Should().Be("100");
		myParam.Type.Should().Be("long");
		debugProtocolHost.Evaluate(frameId, "myParam").Result.Should().Be("100");

		var anotherVar = debugProtocolHost.SetVariable(scopeReference, "anotherVar", "\"changed\"");
		anotherVar.Value.Should().Be("\"changed\"");
		anotherVar.Type.Should().Be("string");
		debugProtocolHost.Evaluate(frameId, "anotherVar").Result.Should().Be("\"changed\"");

		debugProtocolHost.SetVariable(scopeReference, "anotherVar", "null").Value.Should().Be("null");
		debugProtocolHost.Evaluate(frameId, "anotherVar").Result.Should().Be("null");

		debugProtocolHost.SetVariable(scopeReference, "nullableInt", "5").Value.Should().Be("5");
		debugProtocolHost.Evaluate(frameId, "nullableInt").Result.Should().Be("5");
		debugProtocolHost.SetVariable(scopeReference, "nullableIntWithVal", "null").Value.Should().Be("null");
		debugProtocolHost.Evaluate(frameId, "nullableIntWithVal").Result.Should().Be("null");

		debugProtocolHost.SetVariable(scopeReference, "enumVar", "MyEnum.ThirdValue").Value.Should().Be("ThirdValue");
		debugProtocolHost.Evaluate(frameId, "enumVar").Result.Should().Be("ThirdValue");

		var nullableRefType = debugProtocolHost.SetVariable(scopeReference, "nullableRefType", "this");
		nullableRefType.Value.Should().Be("{DebuggableConsoleApp.MyClass}");
		(nullableRefType.VariablesReference ?? 0).Should().BePositive();
		debugProtocolHost.Evaluate(frameId, "nullableRefType == this").Result.Should().Be("true");

		debugProtocolHost.WithVariablesRequest(scopeReference, out var variables);
		variables.Single(v => v.Name == "myInt").Value.Should().Be("13");
		variables.Single(v => v.Name == "myParam").Value.Should().Be("100");
		variables.Single(v => v.Name == "nullableInt").Value.Should().Be("5");
	}

	[Fact]
	public async Task SetVariableRequest_Members_AreAssignedInPlace()
	{
		var startSuspended = true;

		var (debugProtocolHost, adapter, p2) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended);
		using var assertionScope = new AssertionScope();
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(p2.Id)
			.WaitForInitializedEvent();
		debugProtocolHost
			.WithBreakpointsRequest()
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
		debugProtocolHost
			.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTraceResponse)
			.WithScopesRequest(stackTraceResponse.StackFrames!.First().Id, out var scopesResponse);
		var frameId = stackTraceResponse.StackFrames!.First().Id;
		var scopeReference = scopesResponse.Scopes.Single().VariablesReference;
		// A container's variables must have been listed before one of them can be set
		debugProtocolHost.WithVariablesRequest(scopeReference, out var locals);

		var thisReference = locals.Single(v => v.Name == "this").VariablesReference;
		debugProtocolHost.WithVariablesRequest(thisReference, out var thisMembers);

		debugProtocolHost.SetVariable(thisReference, "_instanceField", "7").Value.Should().Be("7");
		debugProtocolHost.Evaluate(frameId, "_instanceField").Result.Should().Be("7");

		debugProtocolHost.SetVariable(thisReference, "IntProperty", "11").Value.Should().Be("11");
		debugProtocolHost.Evaluate(frameId, "IntProperty").Result.Should().Be("11");

		var staticReference = thisMembers.Single(v => v.Name == "Static members").VariablesReference;
		debugProtocolHost.WithVariablesRequest(staticReference, out var staticMembers);
		debugProtocolHost.SetVariable(staticReference, "_counter", "3").Value.Should().Be("3");
		debugProtocolHost.Evaluate(frameId, "_counter").Result.Should().Be("3");

		var arrayReference = thisMembers.Single(v => v.Name == "_intArray").VariablesReference;
		debugProtocolHost.WithVariablesRequest(arrayReference, out var arrayMembers);
		debugProtocolHost.SetVariable(arrayReference, "[1]", "99").Value.Should().Be("99");
		debugProtocolHost.Evaluate(frameId, "_intArray[1]").Result.Should().Be("99");

		var genericReference = thisMembers.Single(v => v.Name == "_myClassWithGeneric").VariablesReference;
		debugProtocolHost.WithVariablesRequest(genericReference, out var genericMembers);
		debugProtocolHost.SetVariable(genericReference, "GenericItemsField", "new[] { 7, 8 }").Value.Should().Be("int[2]");
		debugProtocolHost.Evaluate(frameId, "_myClassWithGeneric.GenericItemsField[1]").Result.Should().Be("8");

		// A field of a struct local is written into the local itself, not a copy of it
		var structReference = locals.Single(v => v.Name == "structVar").VariablesReference;
		debugProtocolHost.WithVariablesRequest(structReference, out var structMembers);
		debugProtocolHost.SetVariable(structReference, "Id", "9").Value.Should().Be("9");
		debugProtocolHost.Evaluate(frameId, "structVar.Id").Result.Should().Be("9");
		debugProtocolHost.SetVariable(structReference, "Name", "\"Renamed\"").Value.Should().Be("\"Renamed\"");
		debugProtocolHost.Evaluate(frameId, "structVar.Name").Result.Should().Be("\"Renamed\"");
	}

	[Fact]
	public async Task SetVariableRequest_InvalidValue_FailsAndLeavesTheVariableUnchanged()
	{
		var startSuspended = true;

		var (debugProtocolHost, adapter, p2) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended);
		using var assertionScope = new AssertionScope();
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(p2.Id)
			.WaitForInitializedEvent();
		debugProtocolHost
			.WithBreakpointsRequest()
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
		debugProtocolHost
			.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTraceResponse)
			.WithScopesRequest(stackTraceResponse.StackFrames!.First().Id, out var scopesResponse);
		var frameId = stackTraceResponse.StackFrames!.First().Id;
		var scopeReference = scopesResponse.Scopes.Single().VariablesReference;
		// A container's variables must have been listed before one of them can be set
		debugProtocolHost.WithVariablesRequest(scopeReference, out var locals);

		var typeMismatch = debugProtocolHost.SendRequest(new SetVariableArguments { VariablesReference = scopeReference, Name = "myInt", Value = "\"abc\"" });
		testOutputHelper.WriteLine($"Type mismatch: {Describe(typeMismatch)}");
		(typeMismatch is DapError).Should().BeTrue();

		var syntaxError = debugProtocolHost.SendRequest(new SetVariableArguments { VariablesReference = scopeReference, Name = "myInt", Value = "1 +" });
		testOutputHelper.WriteLine($"Syntax error: {Describe(syntaxError)}");
		(syntaxError is DapError).Should().BeTrue();

		var unknownName = debugProtocolHost.SendRequest(new SetVariableArguments { VariablesReference = scopeReference, Name = "doesNotExist", Value = "1" });
		testOutputHelper.WriteLine($"Unknown name: {Describe(unknownName)}");
		(unknownName is DapError).Should().BeTrue();

		debugProtocolHost.Evaluate(frameId, "myInt").Result.Should().Be("4");
	}

	private static string Describe<T>(DapResult<T> result) => result switch
	{
		DapError error => $"error '{error.Message}'",
		T response => $"response {response}",
		_ => "null"
	};
}
