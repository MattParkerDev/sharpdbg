using AwesomeAssertions;
using AwesomeAssertions.Execution;
using SharpDbg.DapProtocol;
using SharpDbg.Cli.Tests.Helpers;

namespace SharpDbg.Cli.Tests;

public class EvalTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task SharpDbgCli_EvaluationRequest_Returns()
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
			.WithBreakpointsRequest(22, Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "MyClass.cs"))
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
		debugProtocolHost
			.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTraceResponse)
			.WithScopesRequest(stackTraceResponse.StackFrames!.First().Id, out var scopesResponse);

		scopesResponse.Scopes.Should().HaveCount(1);
		var scope = scopesResponse.Scopes.Single();

		List<Variable> expectedVariables =
		[
			new() {Name = "this", Value = "{DebuggableConsoleApp.MyClass}", Type = "DebuggableConsoleApp.MyClass", EvaluateName = "this", VariablesReference = 3 },
			new() {Name = "myParam", Value = "13", Type = "long", EvaluateName = "myParam" },
			new() {Name = "myInt", Value = "4", Type = "int", EvaluateName = "myInt" },
			new() {Name = "enumVar", Value = "SecondValue", Type = "DebuggableConsoleApp.MyEnum", EvaluateName = "enumVar", VariablesReference = 4 },
			new() {Name = "enumWithFlagsVar", Value = "FlagValue1 | FlagValue3", Type = "DebuggableConsoleApp.MyEnumWithFlags", EvaluateName = "enumWithFlagsVar", VariablesReference = 5 },
			new() {Name = "nullableInt", Value = "null", Type = "int?", EvaluateName = "nullableInt" },
			new() {Name = "structVar", Value = "{DebuggableConsoleApp.MyStruct}", Type = "DebuggableConsoleApp.MyStruct", EvaluateName = "structVar", VariablesReference = 6 },
			new() {Name = "nullableIntWithVal", Value = "4", Type = "int?", EvaluateName = "nullableIntWithVal" },
			new() {Name = "nullableRefType", Value = "null", Type = "DebuggableConsoleApp.MyClass", EvaluateName = "nullableRefType" },
			new() {Name = "anotherVar", Value = "\"asdf\"", Type = "string", EvaluateName = "anotherVar" },
		];

		debugProtocolHost.WithVariablesRequest(scope.VariablesReference, out var variables);

		variables.Should().HaveCount(11);
		//variables.ShouldBeEquivalentToDebuggerVariables(expectedVariables);
		using var ____ = new AssertionScope();

		var stackFrameId = stackTraceResponse.StackFrames!.First().Id;
		var evaluateResponse = debugProtocolHost.Evaluate(stackFrameId, "myInt + 10");
		evaluateResponse.Result.Should().Be("14");
		evaluateResponse.VariablesReference.Should().Be(0);
		debugProtocolHost.Evaluate(stackFrameId, "myInt + myInt").Result.Should().Be("8");
		debugProtocolHost.Evaluate(stackFrameId, "myIntParam + 4").Result.Should().Be("10");
		debugProtocolHost.Evaluate(stackFrameId, "_instanceField + 4").Result.Should().Be("9");
		debugProtocolHost.Evaluate(stackFrameId, "_instanceStaticField + 4").Result.Should().Be("10");
		debugProtocolHost.Evaluate(stackFrameId, "_instanceStaticField = _instanceStaticField + 4").Result.Should().Be("10");
		debugProtocolHost.Evaluate(stackFrameId, "_instanceStaticField").Result.Should().Be("10");
		debugProtocolHost.Evaluate(stackFrameId, "IntProperty + 4").Result.Should().Be("14");
		debugProtocolHost.Evaluate(stackFrameId, "IntStaticProperty + 4").Result.Should().Be("14");
		debugProtocolHost.Evaluate(stackFrameId, "ClassProperty.IntField + 4").Result.Should().Be("10");
		debugProtocolHost.Evaluate(stackFrameId, "this.Get14() + 4").Result.Should().Be("18");
		debugProtocolHost.Evaluate(stackFrameId, "MyClass.IntStaticProperty + 4").Result.Should().Be("14");
		debugProtocolHost.Evaluate(stackFrameId, "DebuggableConsoleApp.MyClass.IntStaticProperty + 4").Result.Should().Be("14");
		debugProtocolHost.Evaluate(stackFrameId, "Namespace1.AnotherClass.IntStaticProperty + 4").Result.Should().Be("14");
		debugProtocolHost.Evaluate(stackFrameId, "this.DoubleNumber(4)").Result.Should().Be("8");
		debugProtocolHost.Evaluate(stackFrameId, "this.DoubleNumber(4f)").Result.Should().Be("8");
		debugProtocolHost.Evaluate(stackFrameId, "Get14()").Result.Should().Be("14");
		debugProtocolHost.Evaluate(stackFrameId, "GetObject().IntProperty").Result.Should().Be("6");
		debugProtocolHost.Evaluate(stackFrameId, "ReadObjectIn(GetObject())").Result.Should().Be("6");
		debugProtocolHost.Evaluate(stackFrameId, "GetObjectByReference().IntProperty").Result.Should().Be("6");
		debugProtocolHost.Evaluate(stackFrameId, "_classField.AddToIntField(ClearClassFieldAndCollect())").Result.Should().Be("6");
		debugProtocolHost.Evaluate(stackFrameId, "WasClearedClassFieldReleased()").Result.Should().Be("true");
		debugProtocolHost.Evaluate(stackFrameId, "new MyClass2().IntProperty").Result.Should().Be("6");
		debugProtocolHost.Evaluate(stackFrameId, "IntProperty.ToString()").Result.Should().Be("\"10\"");
		debugProtocolHost.Evaluate(stackFrameId, "this.TestMethod(4, \"asdf\")").Result.Should().Be("8");
		debugProtocolHost.Evaluate(stackFrameId, "$\"Count = {IntProperty}\"").Result.Should().Be("\"Count = 10\"");
		debugProtocolHost.Evaluate(stackFrameId, "this._classWithDebugDisplay").Result.Should().Be("IntProperty = 14");
		debugProtocolHost.Evaluate(stackFrameId, "_classWithDebugDisplay").Result.Should().Be("IntProperty = 14");
		debugProtocolHost.Evaluate(stackFrameId, "$\"{_instanceField}\"").Result.Should().Be("\"5\"");
		debugProtocolHost.Evaluate(stackFrameId, "_intList").Result.Should().Be("Count = 4");
		debugProtocolHost.Evaluate(stackFrameId, "$\"Count = {_intList.Count}\"").Result.Should().Be("\"Count = 4\"");
		debugProtocolHost.Evaluate(stackFrameId, "_classWithDebugDisplay2").Result.Should().Be("Test = stringValue1");
		debugProtocolHost.Evaluate(stackFrameId, "_classWithDebugDisplay3").Result.Should().Be("Test = stringValue2");
		debugProtocolHost.Evaluate(stackFrameId, "myInt = 5").Result.Should().Be("5");
		debugProtocolHost.Evaluate(stackFrameId, "myInt * 2").Result.Should().Be("10");
		debugProtocolHost.Evaluate(stackFrameId, "enumVar").Result.Should().Be("SecondValue");
		debugProtocolHost.Evaluate(stackFrameId, "enumVar = MyEnum.ThirdValue").Result.Should().Be("ThirdValue");
		debugProtocolHost.Evaluate(stackFrameId, "GenericTypeWithStaticField<string>.IntValue").Result.Should().Be("4");
		debugProtocolHost.Evaluate(stackFrameId, "GenericTypeWithStaticField<string>.IntProperty").Result.Should().Be("5");
		debugProtocolHost.Evaluate(stackFrameId, "IntProperty = 16").Result.Should().Be("16");
		debugProtocolHost.Evaluate(stackFrameId, "IntProperty").Result.Should().Be("16");
		debugProtocolHost.Evaluate(stackFrameId, "Namespace1.AnotherClass.MyStaticMethod()").Result.Should().Be("Count = 1");
		debugProtocolHost.Evaluate(stackFrameId, "Namespace1.AnotherClass.IntStaticProperty = 17").Result.Should().Be("17");
		debugProtocolHost.Evaluate(stackFrameId, "Namespace1.AnotherClass.IntStaticProperty").Result.Should().Be("17");
		var classPropertyResponse = debugProtocolHost.Evaluate(stackFrameId, "ClassProperty");
		classPropertyResponse.Result.Should().Be("{DebuggableConsoleApp.MyClass2}");
		classPropertyResponse.VariablesReference.Should().BePositive();
		debugProtocolHost.WithVariablesRequest(classPropertyResponse.VariablesReference, out var classPropertyMembers);
		classPropertyMembers.Should().HaveCount(3);
		debugProtocolHost.Evaluate(stackFrameId, "structVar.ExtensionMethod()").Result.Should().Be("5");
		debugProtocolHost.Evaluate(stackFrameId, "myInt == 5 && myInt < 10").Result.Should().Be("true");
		debugProtocolHost.Evaluate(stackFrameId, "myInt == 4 && myInt < 10").Result.Should().Be("false");
		debugProtocolHost.Evaluate(stackFrameId, "myInt == 4 || myInt < 10").Result.Should().Be("true");
		// Short-circuit: the RHS would throw a NullReferenceException if it were evaluated
		debugProtocolHost.Evaluate(stackFrameId, "myInt == 4 && nullableRefType.GetHashCode() > 0").Result.Should().Be("false");

		// Conversions must produce correctly-typed runtime values
		debugProtocolHost.Evaluate(stackFrameId, "enumVar = (MyEnum)2").Result.Should().Be("ThirdValue");
		debugProtocolHost.Evaluate(stackFrameId, "myParam = 5").Result.Should().Be("5");
		debugProtocolHost.Evaluate(stackFrameId, "(int)nullableIntWithVal + 1").Result.Should().Be("5");
		debugProtocolHost.Evaluate(stackFrameId, "(int)5.5").Result.Should().Be("5");
		debugProtocolHost.Evaluate(stackFrameId, "(int)enumVar").Result.Should().Be("2");
		debugProtocolHost.Evaluate(stackFrameId, "nullableInt ?? 5").Result.Should().Be("5");
		debugProtocolHost.Evaluate(stackFrameId, "nullableIntWithVal ?? 10").Result.Should().Be("4");
		debugProtocolHost.Evaluate(stackFrameId, "(myInt > 3) == true").Result.Should().Be("true");
		debugProtocolHost.Evaluate(stackFrameId, "(myInt > 5) == true").Result.Should().Be("false");
		debugProtocolHost.Evaluate(stackFrameId, "myInt += 2").Result.Should().Be("7");
		debugProtocolHost.Evaluate(stackFrameId, "GenericTypeWithStaticField<int[]>.IntValue").Result.Should().Be("4");
		debugProtocolHost.Evaluate(stackFrameId, "GenericTypeWithStaticField<DateTime>.IntValue").Result.Should().Be("4");
		debugProtocolHost.Evaluate(stackFrameId, "Array.Empty<DateTime>().Length").Result.Should().Be("0");
		debugProtocolHost.Evaluate(stackFrameId, "new int[2].Length").Result.Should().Be("2");
		debugProtocolHost.Evaluate(stackFrameId, "new MyClass[2].Length").Result.Should().Be("2");
		debugProtocolHost.Evaluate(stackFrameId, "new MyClass[0].Length").Result.Should().Be("0");
		debugProtocolHost.Evaluate(stackFrameId, "new DateTime[0].Length").Result.Should().Be("0");
		debugProtocolHost.Evaluate(stackFrameId, "new DateTime[2].Length").Result.Should().Be("2");
		debugProtocolHost.Evaluate(stackFrameId, "default(int)").Result.Should().Be("0");
		debugProtocolHost.Evaluate(stackFrameId, "default(string)").Result.Should().Be("null");
		debugProtocolHost.Evaluate(stackFrameId, "default(MyClass)").Result.Should().Be("null");
		debugProtocolHost.Evaluate(stackFrameId, "default(int[])").Result.Should().Be("null");
		debugProtocolHost.Evaluate(stackFrameId, "typeof(MyClass).ToString()").Result.Should().Be("\"DebuggableConsoleApp.MyClass\"");
		debugProtocolHost.Evaluate(stackFrameId, "IncrementByRef(ref myInt)").Result.Should().Be("10");
		debugProtocolHost.Evaluate(stackFrameId, "myInt").Result.Should().Be("10");
		debugProtocolHost.Evaluate(stackFrameId, "AssignOut(out myInt)").Result.Should().Be("42");
		debugProtocolHost.Evaluate(stackFrameId, "myInt").Result.Should().Be("42");
		debugProtocolHost.Evaluate(stackFrameId, "AddOneIn(in myInt)").Result.Should().Be("43");
		debugProtocolHost.Evaluate(stackFrameId, "_doubler(4)").Result.Should().Be("8");

		// User-defined operators are resolved from the bound tree (Roslyn), so value-type operands and operators
		// declared on the right operand's type work (structVar.Id == 5)
		debugProtocolHost.Evaluate(stackFrameId, "(structVar + structVar).Id").Result.Should().Be("10");
		debugProtocolHost.Evaluate(stackFrameId, "(5 + structVar).Id").Result.Should().Be("10");
		debugProtocolHost.Evaluate(stackFrameId, "(-structVar).Id").Result.Should().Be("-5");
		debugProtocolHost.Evaluate(stackFrameId, "structVar == structVar").Result.Should().Be("true");
		debugProtocolHost.Evaluate(stackFrameId, "this.DoubleNumber(myInt)").Result.Should().Be("84");

		debugProtocolHost.Evaluate(stackFrameId, "_intList.Where(s => s == 4).ToList()").Result.Should().Be("Count = 1");
		debugProtocolHost.Evaluate(stackFrameId, "_intList.Where(s => s == myInt).ToList()").Result.Should().Be("Count = 0");
		debugProtocolHost.Evaluate(stackFrameId, "_intList.Where(s => s == myInt).Select(s => s).ToList()").Result.Should().Be("Count = 0");
		debugProtocolHost.Evaluate(stackFrameId, "((Func<int, int>)(value => value + 1))(4)").Result.Should().Be("5");

		// Lambdas retain the stopped frame's lexical context, including this and non-public members and types.
		debugProtocolHost.Evaluate(stackFrameId, "_intList.Where(value => value == _instanceField).ToList()").Result.Should().Be("Count = 0");
		debugProtocolHost.Evaluate(stackFrameId, "new[] { this }.Where(value => value == this).Count()").Result.Should().Be("1");
		debugProtocolHost.Evaluate(stackFrameId, "_intList.Select(value => DoubleNumber(value)).Last()").Result.Should().Be("50");
		debugProtocolHost.Evaluate(stackFrameId, "new[] { 1 }.Select(value => typeof(EvaluationLifetimeTracker).Name).Single()").Result.Should().Be("\"EvaluationLifetimeTracker\"");

		// Nested lambdas and method groups retain their generated targets and methods.
		debugProtocolHost.Evaluate(stackFrameId, "new[] { 1, 2 }.Where(value => new[] { value }.Any(inner => inner == value)).Count()").Result.Should().Be("2");
		debugProtocolHost.Evaluate(stackFrameId, "new[] { -1 }.Where(value => value < 0).Select(Math.Abs).Single()").Result.Should().Be("1");

		// All delegates in one expression share the same closure, including captures that are only written.
		debugProtocolHost.Evaluate(stackFrameId, "_intList.Select(value => myInt = value).Last()").Result.Should().Be("25");
		debugProtocolHost.Evaluate(stackFrameId, "myInt").Result.Should().Be("25");
		debugProtocolHost.Evaluate(stackFrameId, "myInt = 42").Result.Should().Be("42");
		debugProtocolHost.Evaluate(stackFrameId, "_intList.Select(value => myInt++).Select(value => myInt).ToList().Last()").Result.Should().Be("46");
		debugProtocolHost.Evaluate(stackFrameId, "myInt").Result.Should().Be("46");
		debugProtocolHost.Evaluate(stackFrameId, "myInt = 42").Result.Should().Be("42");
		debugProtocolHost.Evaluate(stackFrameId, "_intList.Select(value => myInt++).Last() + myInt").Result.Should().Be("85");
		debugProtocolHost.Evaluate(stackFrameId, "myInt").Result.Should().Be("43");
		debugProtocolHost.Evaluate(stackFrameId, "myInt = 42").Result.Should().Be("42");

		// We don't support this.
		// debugProtocolHost.Evaluate(stackFrameId, "new[] { 1 }.Select(delegate(int value) { return value + 1; }).Single()").Result.Should().Be("2");

		debugProtocolHost.Evaluate(stackFrameId, "nullableRefType == null").Result.Should().Be("true");
		debugProtocolHost.Evaluate(stackFrameId, "this == this").Result.Should().Be("true");
		debugProtocolHost.Evaluate(stackFrameId, "anotherVar == \"asdf\"").Result.Should().Be("true");
		debugProtocolHost.Evaluate(stackFrameId, "nullableIntWithVal.HasValue == true").Result.Should().Be("true");
		debugProtocolHost.Evaluate(stackFrameId, "enumVar == MyEnum.ThirdValue").Result.Should().Be("true");
		debugProtocolHost.Evaluate(stackFrameId, "enumVar == MyEnum.SecondValue").Result.Should().Be("false");
		debugProtocolHost.Evaluate(stackFrameId, "Convert.ToByte(myInt) == myInt").Result.Should().Be("true");

		var nullableConversionResponse = debugProtocolHost.Evaluate(stackFrameId, "(int?)myInt");
		nullableConversionResponse.Result.Should().Be("42");
		nullableConversionResponse.Type.Should().Be("int?");
		var enumConversionResponse = debugProtocolHost.Evaluate(stackFrameId, "(MyEnum)(myInt - 40)");
		enumConversionResponse.Result.Should().Be("ThirdValue");
		enumConversionResponse.Type.Should().Be("DebuggableConsoleApp.MyEnum");
		debugProtocolHost.Evaluate(stackFrameId, "enumWithFlagsVar | MyEnumWithFlags.FlagValue2").Result.Should().Be("FlagValue1 | FlagValue2 | FlagValue3");
		debugProtocolHost.Evaluate(stackFrameId, "(int)this.DoubleNumber(4f)").Result.Should().Be("8");
		debugProtocolHost.Evaluate(stackFrameId, "(double)this.DoubleNumber(4f)").Result.Should().Be("8");
		debugProtocolHost.Evaluate(stackFrameId, "this.DoubleNumber(this.DoubleNumber(4f) + 1f)").Result.Should().Be("18");
		debugProtocolHost.Evaluate(stackFrameId, "unchecked((ulong)(uint)(myInt - 43))").Result.Should().Be("4294967295");
		debugProtocolHost.Evaluate(stackFrameId, "this as object as string").Result.Should().Be("null");
		debugProtocolHost.Evaluate(stackFrameId, "((string)(object)this).Length").Result.Should().Contain("InvalidCastException");
		debugProtocolHost.Evaluate(stackFrameId, "(object)myInt != null").Result.Should().Be("true");
		debugProtocolHost.Evaluate(stackFrameId, "(int)(object)myInt").Result.Should().Be("42");
		// unbox.any must reject a box whose runtime type does not match the target type
		debugProtocolHost.Evaluate(stackFrameId, "(long)(object)myInt").Result.Should().Contain("InvalidCastException");
		debugProtocolHost.Evaluate(stackFrameId, "(int)(object)myParam").Result.Should().Contain("InvalidCastException");
		// reference type -> object is an identity conversion (no box), and must return the reference unchanged
		debugProtocolHost.Evaluate(stackFrameId, "(object)anotherVar").Result.Should().Be("\"asdf\"");
		// box/unbox round-trip of an exact enum type
		debugProtocolHost.Evaluate(stackFrameId, "(MyEnum)(object)enumVar").Result.Should().Be("ThirdValue");
		// This is technically not correct behaviour, ie doesn't match behaviour of actual code, but no other debugger handles it. Assert it.
		debugProtocolHost.Evaluate(stackFrameId, "(MyEnum)(object)2").Result.Should().Contain("InvalidCastException");
		debugProtocolHost.Evaluate(stackFrameId, "Array.Empty<int>().Length").Result.Should().Be("0");
		debugProtocolHost.Evaluate(stackFrameId, "int.Parse(anotherVar)").Result.Should().Contain("FormatException");
		debugProtocolHost.Evaluate(stackFrameId, "checked(myInt + int.MaxValue)").Result.Should().Contain("OverflowException");
		debugProtocolHost.Evaluate(stackFrameId, "myInt << (myInt - 10)").Result.Should().Be("42");
		debugProtocolHost.Evaluate(stackFrameId, "-1 >>> (myInt - 41)").Result.Should().Be("2147483647");
		debugProtocolHost.Evaluate(stackFrameId, "ulong.MaxValue / (ulong)(myInt - 40)").Result.Should().Be("9223372036854775807");
		debugProtocolHost.Evaluate(stackFrameId, "0.0 / (myInt - myInt) == double.NaN").Result.Should().Be("false");
		debugProtocolHost.Evaluate(stackFrameId, "TestMethod(myString: \"asdf\", myInt: 4)").Result.Should().Be("8");
		debugProtocolHost.Evaluate(stackFrameId, "_intArray[0]").Result.Should().Be("2");
		debugProtocolHost.Evaluate(stackFrameId, "_intList[0]").Result.Should().Be("1");
		debugProtocolHost.Evaluate(stackFrameId, "nullableRefType?.GetHashCode()").Result.Should().Be("null");

		debugProtocolHost.Evaluate(stackFrameId, "$\"{myInt:D5}\"").Result.Should().Be("\"00042\"");
		debugProtocolHost.Evaluate(stackFrameId, "$\"{myInt,5}\"").Result.Should().Be("\"   42\"");
		debugProtocolHost.Evaluate(stackFrameId, "$\"x{nullableRefType}y\"").Result.Should().Be("\"xy\"");
		debugProtocolHost.Evaluate(stackFrameId, "$\"{ClassProperty}\"").Result.Should().Be("\"DebuggableConsoleApp.MyClass2\"");
		debugProtocolHost.Evaluate(stackFrameId, "$\"{enumVar}\"").Result.Should().Be("\"ThirdValue\"");
		debugProtocolHost.Evaluate(stackFrameId, "$\"{structVar}\"").Result.Should().Be("\"DebuggableConsoleApp.MyStruct\"");
		debugProtocolHost.Evaluate(stackFrameId, "anotherVar + nullableRefType").Result.Should().Be("\"asdf\"");

		debugProtocolHost.Evaluate(stackFrameId, "structVar.Id").Result.Should().Be("5");
		debugProtocolHost.Evaluate(stackFrameId, "structVar.Id = 6").Result.Should().Be("6");
		debugProtocolHost.Evaluate(stackFrameId, "structVar.Id").Result.Should().Be("6");
		debugProtocolHost.Evaluate(stackFrameId, "structVar = default(MyStruct)").Result.Should().Be("{DebuggableConsoleApp.MyStruct}");
		debugProtocolHost.Evaluate(stackFrameId, "structVar.Id").Result.Should().Be("0");

		// stobj: assigning a struct value to a field (stfld)
		debugProtocolHost.Evaluate(stackFrameId, "structVar.Id = 5").Result.Should().Be("5");
		debugProtocolHost.Evaluate(stackFrameId, "_structField = structVar").Result.Should().Be("{DebuggableConsoleApp.MyStruct}");
		debugProtocolHost.Evaluate(stackFrameId, "_structField.Id").Result.Should().Be("5");

		// stobj: struct value copied out of a field back into a local
		debugProtocolHost.Evaluate(stackFrameId, "structVar = _structField").Result.Should().Be("{DebuggableConsoleApp.MyStruct}");
		debugProtocolHost.Evaluate(stackFrameId, "structVar.Id").Result.Should().Be("5");

		// ldobj: struct value loaded through a ref-return (call + ldobj + stloc)
		debugProtocolHost.Evaluate(stackFrameId, "structVar = GetStructByRef()").Result.Should().Be("{DebuggableConsoleApp.MyStruct}");
		debugProtocolHost.Evaluate(stackFrameId, "structVar.Id").Result.Should().Be("5");

		// stobj: struct value stored through a ref-return (call + ldloc + stobj)
		debugProtocolHost.Evaluate(stackFrameId, "GetStructByRef() = structVar").Result.Should().Be("{DebuggableConsoleApp.MyStruct}");
		debugProtocolHost.Evaluate(stackFrameId, "GetStructByRef().Id").Result.Should().Be("5");

		// unboxing conversion: cast-to-struct used as the receiver of an instance method call
		debugProtocolHost.Evaluate(stackFrameId, "((MyStruct)(object)structVar).GetId()").Result.Should().Be("5");

		debugProtocolHost.Evaluate(stackFrameId, "default(MyStruct).Id").Result.Should().Be("0");
		debugProtocolHost.Evaluate(stackFrameId, "sizeof(int)").Result.Should().Be("4");
		debugProtocolHost.Evaluate(stackFrameId, "typeof(int[]).ToString()").Result.Should().Be("\"System.Int32[]\"");
		debugProtocolHost.Evaluate(stackFrameId, "typeof(System.Collections.Generic.List<int>).ToString()").Result.Should().Be("\"System.Collections.Generic.List`1[System.Int32]\"");
		debugProtocolHost.Evaluate(stackFrameId, "typeof(MyClassContainingAnotherClass.MyNestedClass).ToString()").Result.Should().Be("\"DebuggableConsoleApp.MyClassContainingAnotherClass+MyNestedClass\"");
		debugProtocolHost.Evaluate(stackFrameId, "TryGetObject(out var objectResult) ? objectResult.IntProperty : 0").Result.Should().Be("6");

		debugProtocolHost.Evaluate(stackFrameId, "new OverloadResolutionGeneric<int>().Pick(\"hello\")").Result.Should().Be("5");
		debugProtocolHost.Evaluate(stackFrameId, "myInt = 42").Result.Should().Be("42");
		debugProtocolHost.Evaluate(stackFrameId, "OverloadResolutionByRef.Pick(ref myInt)").Result.Should().Be("42");

		debugProtocolHost.Evaluate(stackFrameId, "anotherVar = \"updated\"").Result.Should().Be("\"updated\"");
		debugProtocolHost.Evaluate(stackFrameId, "anotherVar").Result.Should().Be("\"updated\"");
		debugProtocolHost.Evaluate(stackFrameId, "anotherVar == \"updated\"").Result.Should().Be("true");

		// String reference identity: reading a debuggee string must preserve the original reference instead of
		// materializing a fresh string, so identity comparisons and assignments preserve aliasing.
		debugProtocolHost.Evaluate(stackFrameId, "IsSameString(anotherVar, anotherVar)").Result.Should().Be("true");
		debugProtocolHost.Evaluate(stackFrameId, "object.ReferenceEquals(anotherVar, anotherVar)").Result.Should().Be("true");
		debugProtocolHost.Evaluate(stackFrameId, "anotherVar = _name").Result.Should().Be("\"TestName\"");
		debugProtocolHost.Evaluate(stackFrameId, "IsSameString(anotherVar, _name)").Result.Should().Be("true");

		debugProtocolHost.Evaluate(stackFrameId, "nullableRefType = new MyClass()").Result.Should().Be("{DebuggableConsoleApp.MyClass}");
		debugProtocolHost.Evaluate(stackFrameId, "nullableRefType == null").Result.Should().Be("false");
		debugProtocolHost.Evaluate(stackFrameId, "nullableRefType = null").Result.Should().Be("null");
		debugProtocolHost.Evaluate(stackFrameId, "nullableRefType == null").Result.Should().Be("true");

		debugProtocolHost.Evaluate(stackFrameId, "_classField = null").Result.Should().Be("null");
		debugProtocolHost.Evaluate(stackFrameId, "_classField == null").Result.Should().Be("true");

		debugProtocolHost.Evaluate(stackFrameId, "(new string[1])[0] = \"array-string\"").Result.Should().Be("\"array-string\"");
		debugProtocolHost.Evaluate(stackFrameId, "(new MyClass2[1])[0] = null").Result.Should().Be("null");

		// Comparison results and conversions keep their C# type when formatted
		debugProtocolHost.Evaluate(stackFrameId, "$\"{InterpreterSemantics.UIntMax > 1}\"").Result.Should().Be("\"True\"");
		debugProtocolHost.Evaluate(stackFrameId, "$\"{(char)(myInt + 78)}\"").Result.Should().Be("\"x\"");
		debugProtocolHost.Evaluate(stackFrameId, "InterpreterSemantics.Text + InterpreterSemantics.Letter").Result.Should().Be("\"abcx\"");
		debugProtocolHost.Evaluate(stackFrameId, "InterpreterSemantics.Letter + InterpreterSemantics.Text").Result.Should().Be("\"xabc\"");
		debugProtocolHost.Evaluate(stackFrameId, "InterpreterSemantics.Letter.ToString()").Result.Should().Be("\"x\"");
		// Enums compare and convert through their underlying type
		debugProtocolHost.Evaluate(stackFrameId, "InterpreterSemantics.SignedKind == SByteEnum.Negative").Result.Should().Be("true");
		debugProtocolHost.Evaluate(stackFrameId, "(int)InterpreterSemantics.SignedKind").Result.Should().Be("-1");
		debugProtocolHost.Evaluate(stackFrameId, "InterpreterSemantics.UnsignedKind == UShortEnum.Large").Result.Should().Be("true");
		debugProtocolHost.Evaluate(stackFrameId, "(int)InterpreterSemantics.UnsignedKind").Result.Should().Be("40000");
		// Unchecked unsigned arithmetic wraps
		debugProtocolHost.Evaluate(stackFrameId, "InterpreterSemantics.UIntMax - 1").Result.Should().Be("4294967294");
		debugProtocolHost.Evaluate(stackFrameId, "InterpreterSemantics.UIntMax + 1").Result.Should().Be("0");
		// A long array index or length
		debugProtocolHost.Evaluate(stackFrameId, "InterpreterSemantics.Numbers[InterpreterSemantics.LongIndex]").Result.Should().Be("1");
		debugProtocolHost.Evaluate(stackFrameId, "new byte[InterpreterSemantics.LongIndex].Length").Result.Should().Be("1");
		// Storing an int32 stack value into a bool
		debugProtocolHost.Evaluate(stackFrameId, "InterpreterSemantics.Flag = true").Result.Should().Be("true");
		debugProtocolHost.Evaluate(stackFrameId, "InterpreterSemantics.Flag").Result.Should().Be("true");
		// Constructors and methods on a value type run against its storage
		debugProtocolHost.Evaluate(stackFrameId, "InterpreterSemantics.Maybe = 8").Result.Should().Be("8");
		debugProtocolHost.Evaluate(stackFrameId, "InterpreterSemantics.Maybe").Result.Should().Be("8");
		debugProtocolHost.Evaluate(stackFrameId, "nullableInt = 9").Result.Should().Be("9");
		debugProtocolHost.Evaluate(stackFrameId, "nullableInt").Result.Should().Be("9");
		debugProtocolHost.Evaluate(stackFrameId, "InterpreterSemantics.Holder.Nullable = 44.0").Result.Should().Be("44");
		debugProtocolHost.Evaluate(stackFrameId, "InterpreterSemantics.Holder.Nullable").Result.Should().Be("44");
		debugProtocolHost.Evaluate(stackFrameId, "InterpreterSemantics.Holder.Box = new WidthStruct(7)").Result.Should().Be("{DebuggableConsoleApp.WidthStruct}");
		debugProtocolHost.Evaluate(stackFrameId, "InterpreterSemantics.Holder.Box.Width").Result.Should().Be("7");
		debugProtocolHost.Evaluate(stackFrameId, "new WidthStruct(5).Width").Result.Should().Be("5");
		// out var read back through unbox.any
		debugProtocolHost.Evaluate(stackFrameId, "int.TryParse(\"7\", out var parsed) ? parsed : -1").Result.Should().Be("7");
		// Array initializers copy the data the evaluation assembly carries
		debugProtocolHost.Evaluate(stackFrameId, "new[] { 1, 2, 3 }.Length").Result.Should().Be("3");
		debugProtocolHost.Evaluate(stackFrameId, "new[] { 1, 2, 3 }[1]").Result.Should().Be("2");
		debugProtocolHost.Evaluate(stackFrameId, "new[] { 1.5, 2.5, 3.5 }[2]").Result.Should().Be("3.5");
		debugProtocolHost.Evaluate(stackFrameId, "new long[] { 1, 2, 3, 4 }[3]").Result.Should().Be("4");
		// Pattern variables are not supported, and say so
		debugProtocolHost.Evaluate(stackFrameId, "InterpreterSemantics.BoxedLong is long value ? value : 0").Result.Should().Contain("pattern");
		debugProtocolHost.Evaluate(stackFrameId, "InterpreterSemantics.BoxedLong is long").Result.Should().Be("true");
	}

	[Fact]
	public async Task SharpDbgCli_EvaluationRequest_ShadowedLocal_ResolvesCorrectSlot()
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
			.WithBreakpointsRequest(48, Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "Lambdas", "MyLambdaClass.cs"))
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
		debugProtocolHost
			.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTraceResponse)
			.WithScopesRequest(stackTraceResponse.StackFrames!.First().Id, out var scopesResponse);

		var stackFrameId = stackTraceResponse.StackFrames!.First().Id;

		// 'value' must resolve to the innermost lambda's parameter (200), not the hoisted method-local 'value' (100)
		debugProtocolHost.Evaluate(stackFrameId, "value").Result.Should().Be("200");
	}

	[Fact]
	public async Task SharpDbgCli_EvaluationRequest_GenericMethod_Evaluates()
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
			.WithBreakpointsRequest(7, Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "MyClassWithGenericMethod.cs"))
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
		debugProtocolHost
			.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTraceResponse)
			.WithScopesRequest(stackTraceResponse.StackFrames!.First().Id, out var scopesResponse);

		var stackFrameId = stackTraceResponse.StackFrames!.First().Id;

		debugProtocolHost.Evaluate(stackFrameId, "default(T)").Result.Should().Be("0");
		debugProtocolHost.Evaluate(stackFrameId, "arg").Result.Should().Be("4");
		debugProtocolHost.Evaluate(stackFrameId, "typeof(T)").Result.Should().Be("System.Int32");
	}

	[Fact]
	public async Task SharpDbgCli_EvaluationRequest_StaticClassMethod_EvaluatesCorrectFieldValue()
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
			.WithBreakpointsRequest([9], Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "MyStaticClass.cs"))
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent();
		debugProtocolHost
			.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTraceResponse)
			.WithScopesRequest(stackTraceResponse.StackFrames!.First().Id, out var scopesResponse);

		var stackFrameId = stackTraceResponse.StackFrames!.First().Id;

		debugProtocolHost.Evaluate(stackFrameId, "_testInt").Result.Should().Be("4");
	}

	// At the entry point the debuggee has not loaded System.Linq, so Roslyn cannot see Enumerable until it is loaded
	[Fact]
	public async Task SharpDbgCli_EvaluationRequest_LinqBeforeSystemLinqIsLoaded_LoadsTheAssembly()
	{
		var (host, adapter) = TestHelper.GetLaunchDebugProtocolHostInProc(testOutputHelper);
		using var _ = adapter;
		using var __ = host;
		var program = Path.JoinFromGitRoot("artifacts", "bin", "TopLevelStatementsConsoleApp", "debug", "TopLevelStatementsConsoleApp.dll");

		await host
			.WithInitializeRequest()
			.WithLaunchRequest(program, stopAtEntry: true, justMyCode: true)
			.WaitForInitializedEvent();
		host.WithConfigurationDoneRequest();

		var stoppedEvent = await host.WaitForStoppedEvent();
		var stackFrameId = host.GetTopStackFrame(stoppedEvent.ThreadId!.Value).Id;

		host.Evaluate(stackFrameId, "new[] { 1u, 2u }.Select(x => x * 2).Sum(x => x)").Result.Should().Be("6");
		host.Evaluate(stackFrameId, "Enumerable.Range(1, 3).Sum()").Result.Should().Be("6");
	}
}
