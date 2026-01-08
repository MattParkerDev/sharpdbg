using AwesomeAssertions;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using SharpDbg.Cli.Tests.Helpers;

namespace SharpDbg.Cli.Tests;

public class VariablesTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
    public async Task SyncMethod_VariablesRequest_ReturnsCorrectVariables()
    {
	    var startSuspended = true;

	    var (debugProtocolHost, initializedEventTcs, stoppedEventTcs, adapter, p2) = TestHelper.GetRunningDebugProtocolHostInProc(testOutputHelper, startSuspended);
	    using var _ = adapter;
	    using var __ = new ProcessKiller(p2);

	    await debugProtocolHost
		    .WithInitializeRequest()
		    .WithAttachRequest(p2.Id)
		    .WaitForInitializedEvent(initializedEventTcs);
	    debugProtocolHost
		    .WithBreakpointsRequest()
		    .WithConfigurationDoneRequest()
		    .WithOptionalResumeRuntime(p2.Id, startSuspended);

	    var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(stoppedEventTcs);
	    debugProtocolHost
		    .WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTraceResponse)
		    .WithScopesRequest(stackTraceResponse.StackFrames!.First().Id, out var scopesResponse);

	    scopesResponse.Scopes.Should().HaveCount(1);
	    var scope = scopesResponse.Scopes.Single();

	    List<Variable> expectedVariables =
	    [
		    new() {Name = "this", Value = "{DebuggableConsoleApp.MyClass}", Type = "DebuggableConsoleApp.MyClass", EvaluateName = "this", VariablesReference = 3 },
		    new() {Name = "myParam", Value = "13", Type = "long", EvaluateName = "myParam" },
		    new() {Name = "myIntParam", Value = "6", Type = "int", EvaluateName = "myIntParam" },
		    new() {Name = "myInt", Value = "4", Type = "int", EvaluateName = "myInt" },
		    new() {Name = "enumVar", Value = "SecondValue", Type = "DebuggableConsoleApp.MyEnum", EvaluateName = "enumVar", VariablesReference = 4 },
		    new() {Name = "enumWithFlagsVar", Value = "FlagValue1 | FlagValue3", Type = "DebuggableConsoleApp.MyEnumWithFlags", EvaluateName = "enumWithFlagsVar", VariablesReference = 5 },
		    new() {Name = "nullableInt", Value = "null", Type = "int?", EvaluateName = "nullableInt" },
		    new() {Name = "structVar", Value = "{DebuggableConsoleApp.MyStruct}", Type = "DebuggableConsoleApp.MyStruct", EvaluateName = "structVar", VariablesReference = 6 },
		    new() {Name = "nullableIntWithVal", Value = "4", Type = "int?", EvaluateName = "nullableIntWithVal" },
		    new() {Name = "nullableRefType", Value = "null", Type = "DebuggableConsoleApp.MyClass", EvaluateName = "nullableRefType" },
		    new() {Name = "anotherVar", Value = "asdf", Type = "string", EvaluateName = "anotherVar" },
	    ];

	    debugProtocolHost.WithVariablesRequest(scope.VariablesReference, out var variables);

	    variables.Should().HaveCount(11);
	    variables.Should().BeEquivalentTo(expectedVariables);
	    debugProtocolHost.AssertStructMemberVariables(variables.Single(s => s.Name == "structVar").VariablesReference);
	    debugProtocolHost.AssertInstanceThisInstanceVariables(variables.Single(s => s.Name == "this").VariablesReference);

	    List<Variable> expectedEnumVariables =
	    [
		    new() {Name = "Static members", Value = "", Type = "", EvaluateName = "Static members", VariablesReference = 34, PresentationHint = new VariablePresentationHint { Kind = VariablePresentationHint.KindValue.Class }},
		    new() {Name = "value__", Value = "1", Type = "int", EvaluateName = "value__" },
	    ];

	    debugProtocolHost.WithVariablesRequest(variables.Single(s => s.Name == "enumVar").VariablesReference, out var enumNestedVariables);
	    enumNestedVariables.Should().BeEquivalentTo(expectedEnumVariables);

	    List<Variable> expectedEnumStaticMemberVariables =
	    [
		    new() { Name = "FirstValue", Value = "0", Type = "int", EvaluateName = "FirstValue" },
		    new() { Name = "SecondValue", Value = "1", Type = "int", EvaluateName = "SecondValue" },
		    new() { Name = "ThirdValue", Value = "2", Type = "int", EvaluateName = "ThirdValue" },
	    ];

	    debugProtocolHost.WithVariablesRequest(enumNestedVariables.Single(s => s.Name == "Static members").VariablesReference, out var enumStaticVariables);
	    enumStaticVariables.Should().BeEquivalentTo(expectedEnumStaticMemberVariables);
	    // TODO: Assert that none of the variable references are the same (other than 0)

	    var stoppedEvent2 = await debugProtocolHost
		    .WithContinueRequest()
		    .WaitForStoppedEvent(stoppedEventTcs);
	    debugProtocolHost
		    .WithStackTraceRequest(stoppedEvent2.ThreadId!.Value, out var stackTraceResponse2)
		    .WithScopesRequest(stackTraceResponse2.StackFrames!.First().Id, out var scopesResponse2)
		    .WithVariablesRequest(scopesResponse2.Scopes.Single().VariablesReference, out var variables2);
	    // Assert the variables reference count resets on continue, by asserting the variables are the same as the first time (code is in a while loop)
	    variables2.Should().BeEquivalentTo(expectedVariables);
    }
}

file static class TestExtensions
{
	public static void AssertStructMemberVariables(this DebugProtocolHost debugProtocolHost, int variablesReference)
	{
		List<Variable> expectedVariables =
		[
			new() { Name = "Id", EvaluateName = "Id", Value = "5", Type = "int" },
			new() { Name = "Name", EvaluateName = "Name", Value = "StructName", Type = "string" },
		];
		debugProtocolHost.WithVariablesRequest(variablesReference, out var structMemberVariables);
		structMemberVariables.Should().BeEquivalentTo(expectedVariables);
	}
	public static void AssertInstanceThisInstanceVariables(this DebugProtocolHost debugProtocolHost, int variablesReference)
	{
		List<Variable> expectedVariables =
		[
			new() { Name = "_name", EvaluateName = "_name", Value = "TestName", Type = "string" },
			new() { Name = "_classField", EvaluateName = "_classField", Value = "{DebuggableConsoleApp.MyClass3}", Type = "DebuggableConsoleApp.MyClass3", VariablesReference = 7 },
			new() { Name = "ClassProperty", EvaluateName = "ClassProperty", Value = "{DebuggableConsoleApp.MyClass2}", Type = "DebuggableConsoleApp.MyClass2", VariablesReference = 15 },
			new() { Name = "ClassProperty2", EvaluateName = "ClassProperty2", Value = "{DebuggableConsoleApp.MyClass2}", Type = "DebuggableConsoleApp.MyClass2", VariablesReference = 16 },
			new() { Name = "_intList", EvaluateName = "_intList", Value = "Count = 4", Type = "System.Collections.Generic.List<int>", VariablesReference = 8 },
			new() { Name = "_intArray", EvaluateName = "_intArray", Value = "int[4]", Type = "int[]", VariablesReference = 9 },
			new() { Name = "_instanceField", EvaluateName = "_instanceField", Value = "5", Type = "int" },
			new() { Name = "IntProperty", EvaluateName = "IntProperty", Value = "10", Type = "int" },
			new() { Name = "_classWithDebugDisplay", EvaluateName = "_classWithDebugDisplay", Value = "IntProperty = 14", Type = "DebuggableConsoleApp.ClassWithDebugDisplay", VariablesReference = 10 },
			new() { Name = "_classWithDebugDisplay2", EvaluateName = "_classWithDebugDisplay2", Value = "Test = stringValue1", Type = "DebuggableConsoleApp.ClassWithDebugDisplay2", VariablesReference = 11 },
			new() { Name = "_classWithDebugDisplay3", EvaluateName = "_classWithDebugDisplay3", Value = "Test = stringValue2", Type = "DebuggableConsoleApp.ClassWithDebugDisplay3", VariablesReference = 12 },
			new() { Name = "_myClassWithGeneric", EvaluateName = "_myClassWithGeneric", Value = "{DebuggableConsoleApp.MyClassWithGeneric<int>}", Type = "DebuggableConsoleApp.MyClassWithGeneric<int>", VariablesReference = 13 },
			new() { Name = "_intDictionary", EvaluateName = "_intDictionary", Value = "Count = 3", Type = "System.Collections.Generic.Dictionary<int, int>", VariablesReference = 14 },
			new() { Name = "FieldFromBase", EvaluateName = "FieldFromBase", Value = "42", Type = "int" },
			new() { Name = "PropertyFromBase", EvaluateName = "PropertyFromBase", Value = "84", Type = "int" },
		    new() { Name = "Static members", Value = "", Type = "", EvaluateName = "Static members", VariablesReference = 17, PresentationHint = new VariablePresentationHint { Kind = VariablePresentationHint.KindValue.Class }},
		];
		debugProtocolHost.WithVariablesRequest(variablesReference, out var thisInstanceVariables);
		thisInstanceVariables.Should().BeEquivalentTo(expectedVariables);
		debugProtocolHost.AssertIntArrayVariables(thisInstanceVariables.Single(s => s.Name == "_intArray").VariablesReference);
		debugProtocolHost.AssertInstanceThisStaticVariables(thisInstanceVariables.Single(s => s.Name == "Static members").VariablesReference);
		debugProtocolHost.AssertClassWithDebuggerTypeProxyVariables(thisInstanceVariables.Single(s => s.Name == "_classWithDebugDisplay").VariablesReference);
		debugProtocolHost.AssertGenericClassVariables(thisInstanceVariables.Single(s => s.Name == "_myClassWithGeneric").VariablesReference);
		debugProtocolHost.AssertIntListVariables(thisInstanceVariables.Single(s => s.Name == "_intList").VariablesReference);
		debugProtocolHost.AssertDictionaryVariables(thisInstanceVariables.Single(s => s.Name == "_intDictionary").VariablesReference);
		debugProtocolHost.AssertClassWithFieldOfNestedClassType_Variables(thisInstanceVariables.Single(s => s.Name == "_classField").VariablesReference);
		debugProtocolHost.AssertPropertyStoredClass_Variables(thisInstanceVariables.Single(s => s.Name == "ClassProperty").VariablesReference);
	}

	public static void AssertInstanceThisStaticVariables(this DebugProtocolHost debugProtocolHost, int variablesReference)
	{
		List<Variable> expectedVariables =
		[
			new() { Name = "_counter", EvaluateName = "_counter", Value = "1", Type = "int" },
			new() { Name = "IntStaticProperty", EvaluateName = "IntStaticProperty", Value = "10", Type = "int" },
			new() { Name = "StaticClassProperty", EvaluateName = "StaticClassProperty", Value = "{DebuggableConsoleApp.MyClass2}", Type = "DebuggableConsoleApp.MyClass2", VariablesReference = 23 },
			new() { Name = "_staticClassField", EvaluateName = "_staticClassField", Value = "{DebuggableConsoleApp.MyClass2}", Type = "DebuggableConsoleApp.MyClass2", VariablesReference = 18 },
			new() { Name = "_staticIntList", EvaluateName = "_staticIntList", Value = "Count = 4", Type = "System.Collections.Generic.List<int>", VariablesReference = 19 },
			new() { Name = "_fieldDictionary", EvaluateName = "_fieldDictionary", Value = "Count = 0", Type = "System.Collections.Generic.Dictionary<DebuggableConsoleApp.MyClass2, DebuggableConsoleApp.MyClass>", VariablesReference = 20 },
			new() { Name = "_utcNow", EvaluateName = "_utcNow", Value = "{System.DateTime}", Type = "System.DateTime", VariablesReference = 21 },
			new() { Name = "_nullableUtcNow", EvaluateName = "_nullableUtcNow", Value = "{System.DateTime}", Type = "System.DateTime?", VariablesReference = 22 },
			new() { Name = "_instanceStaticField", EvaluateName = "_instanceStaticField", Value = "6", Type = "int" },
			new() { Name = "StaticFieldFromBase", EvaluateName = "StaticFieldFromBase", Value = "168", Type = "int" },
		];
		debugProtocolHost.WithVariablesRequest(variablesReference, out var instanceThisStaticVariables);
		instanceThisStaticVariables.Should().BeEquivalentTo(expectedVariables);
	}

	private static readonly VariablePresentationHint _arrayElementPresentationHint = new() { Kind = VariablePresentationHint.KindValue.Data };
	public static void AssertIntArrayVariables(this DebugProtocolHost debugProtocolHost, int variablesReference)
	{
		List<Variable> expectedVariables =
		[
			new() { Name = "[0]", EvaluateName = "[0]", Value = "2", Type = "int", PresentationHint = _arrayElementPresentationHint },
			new() { Name = "[1]", EvaluateName = "[1]", Value = "3", Type = "int", PresentationHint = _arrayElementPresentationHint },
			new() { Name = "[2]", EvaluateName = "[2]", Value = "5", Type = "int", PresentationHint = _arrayElementPresentationHint },
			new() { Name = "[3]", EvaluateName = "[3]", Value = "7", Type = "int", PresentationHint = _arrayElementPresentationHint },
		];
		debugProtocolHost.WithVariablesRequest(variablesReference, out var intArrayVariables);
		intArrayVariables.Should().BeEquivalentTo(expectedVariables);
	}

	public static void AssertGenericClassVariables(this DebugProtocolHost debugProtocolHost, int variablesReference)
	{
		List<Variable> expectedVariables =
		[
			new() { Name = "GenericItemsField", EvaluateName = "GenericItemsField", Value = "int[1]", Type = "int[]", VariablesReference = 25 },
			new() { Name = "GenericItems", EvaluateName = "GenericItems", Value = "int[1]", Type = "int[]", VariablesReference = 26 },
		];
		debugProtocolHost.WithVariablesRequest(variablesReference, out var genericClassVariables);
		genericClassVariables.Should().BeEquivalentTo(expectedVariables);
	}

	public static void AssertIntListVariables(this DebugProtocolHost debugProtocolHost, int variablesReference)
	{
		List<Variable> expectedVariables =
		[
			new() { Name = "[0]", EvaluateName = "[0]", Value = "1", Type = "int", PresentationHint = _arrayElementPresentationHint },
			new() { Name = "[1]", EvaluateName = "[1]", Value = "4", Type = "int", PresentationHint = _arrayElementPresentationHint },
			new() { Name = "[2]", EvaluateName = "[2]", Value = "8", Type = "int", PresentationHint = _arrayElementPresentationHint },
			new() { Name = "[3]", EvaluateName = "[3]", Value = "25", Type = "int", PresentationHint = _arrayElementPresentationHint },
			new() { Name = "Raw View", EvaluateName = "Raw View", Value = "", Type = "", VariablesReference = 27, PresentationHint = new VariablePresentationHint { Kind = VariablePresentationHint.KindValue.Class } },
		];
		debugProtocolHost.WithVariablesRequest(variablesReference, out var intListVariables);
		intListVariables.Should().BeEquivalentTo(expectedVariables);
	}

	public static void AssertDictionaryVariables(this DebugProtocolHost debugProtocolHost, int variablesReference)
	{
		List<Variable> expectedVariables =
		[
			new() { Name = "[0]", EvaluateName = "[0]", Value = "[5] = 50", Type = "System.Collections.Generic.DebugViewDictionaryItem<int, int>", VariablesReference = 28, PresentationHint = _arrayElementPresentationHint },
			new() { Name = "[1]", EvaluateName = "[1]", Value = "[10] = 100", Type = "System.Collections.Generic.DebugViewDictionaryItem<int, int>", VariablesReference = 29, PresentationHint = _arrayElementPresentationHint },
			new() { Name = "[2]", EvaluateName = "[2]", Value = "[15] = 150", Type = "System.Collections.Generic.DebugViewDictionaryItem<int, int>", VariablesReference = 30, PresentationHint = _arrayElementPresentationHint },
			new() { Name = "Raw View", EvaluateName = "Raw View", Value = "", Type = "", VariablesReference = 31, PresentationHint = new VariablePresentationHint { Kind = VariablePresentationHint.KindValue.Class } },
		];
		debugProtocolHost.WithVariablesRequest(variablesReference, out var dictionaryVariables);
		dictionaryVariables.Should().BeEquivalentTo(expectedVariables);
	}

	public static void AssertClassWithFieldOfNestedClassType_Variables(this DebugProtocolHost debugProtocolHost, int variablesReference)
	{
		List<Variable> expectedVariables =
		[
			new() { Name = "IntField", EvaluateName = "IntField", Value = "6", Type = "int" },
			new() { Name = "IntProperty", EvaluateName = "IntProperty", Value = "6", Type = "int" },
			new() { Name = "MyProperty", EvaluateName = "MyProperty", Value = "Hello", Type = "string" },
			new() { Name = "NestedClassProperty", EvaluateName = "NestedClassProperty", Value = "{DebuggableConsoleApp.MyClassContainingAnotherClass.MyNestedClass}", Type = "DebuggableConsoleApp.MyClassContainingAnotherClass.MyNestedClass", VariablesReference = 32 },
			new() { Name = "NestedGenericClassProperty", EvaluateName = "NestedGenericClassProperty", Value = "{DebuggableConsoleApp.MyGenericClassContainingAnotherGenericClass<string, int>.MyNestedGenericClass<long, float>}", Type = "DebuggableConsoleApp.MyGenericClassContainingAnotherGenericClass<string, int>.MyNestedGenericClass<long, float>", VariablesReference = 33 },
		];
		debugProtocolHost.WithVariablesRequest(variablesReference, out var classWithNestedClassFieldVariables);
		classWithNestedClassFieldVariables.Should().BeEquivalentTo(expectedVariables);
	}

	public static void AssertPropertyStoredClass_Variables(this DebugProtocolHost debugProtocolHost, int variablesReference)
	{
		List<Variable> expectedVariables =
		[
			new() { Name = "IntField", EvaluateName = "IntField", Value = "6", Type = "int" },
			new() { Name = "IntProperty", EvaluateName = "IntProperty", Value = "6", Type = "int" },
			new() { Name = "MyProperty", EvaluateName = "MyProperty", Value = "Hello", Type = "string" },
		];
		debugProtocolHost.WithVariablesRequest(variablesReference, out var classWithNestedClassFieldVariables);
		classWithNestedClassFieldVariables.Should().BeEquivalentTo(expectedVariables);
	}

	public static void AssertClassWithDebuggerTypeProxyVariables(this DebugProtocolHost debugProtocolHost, int variablesReference)
	{
		List<Variable> expectedVariables =
		[
			new() { Name = "IntPropertyViaDebugView", EvaluateName = "IntPropertyViaDebugView", Value = "14", Type = "int" },
			new() { Name = "[0]", EvaluateName = "[0]", Value = "2", Type = "int", PresentationHint = _arrayElementPresentationHint },
			new() { Name = "[1]", EvaluateName = "[1]", Value = "3", Type = "int", PresentationHint = _arrayElementPresentationHint },
			new() { Name = "[2]", EvaluateName = "[2]", Value = "5", Type = "int", PresentationHint = _arrayElementPresentationHint },
			new() { Name = "[3]", EvaluateName = "[3]", Value = "7", Type = "int", PresentationHint = _arrayElementPresentationHint },
			new() { Name = "Raw View", EvaluateName = "Raw View", Value = "", Type = "", VariablesReference = 24, PresentationHint = new VariablePresentationHint { Kind = VariablePresentationHint.KindValue.Class } },
		];
		debugProtocolHost.WithVariablesRequest(variablesReference, out var classWithDebuggerTypeProxyVariables);
		classWithDebuggerTypeProxyVariables.Should().BeEquivalentTo(expectedVariables);
	}
}
