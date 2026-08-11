using AwesomeAssertions;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using SharpDbg.Cli.Tests.Helpers;

namespace SharpDbg.Cli.Tests;

public class EvalTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task SharpDbgCli_EvaluationRequest_Returns()
	{
		var startSuspended = true;

		var (debugProtocolHost, initializedEventTcs, debugEventTcs, adapter, p2) = TestHelper.GetRunningDebugProtocolHostInProc(testOutputHelper, startSuspended);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(p2.Id)
			.WaitForInitializedEvent(initializedEventTcs);
		debugProtocolHost
			.WithBreakpointsRequest(22, Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "MyClass.cs"))
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(debugEventTcs);
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
		//variables.Should().BeEquivalentTo(expectedVariables);

		var stackFrameId = stackTraceResponse.StackFrames!.First().Id;
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "myInt + 10", out var evaluateResponse);
		evaluateResponse.Result.Should().Be("14");
		evaluateResponse.VariablesReference.Should().Be(0);
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "myInt + myInt", out var evaluateResponse2);
		evaluateResponse2.Result.Should().Be("8");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "myIntParam + 4", out var evaluateResponse3);
		evaluateResponse3.Result.Should().Be("10");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "_instanceField + 4", out var evaluateResponse4);
		evaluateResponse4.Result.Should().Be("9");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "_instanceStaticField + 4", out var evaluateResponse5);
		evaluateResponse5.Result.Should().Be("10");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "_instanceStaticField = _instanceStaticField + 4", out var evaluateResponse6);
		evaluateResponse6.Result.Should().Be("10");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "_instanceStaticField", out var evaluateResponse7);
		evaluateResponse7.Result.Should().Be("10");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "IntProperty + 4", out var evaluateResponse8);
		evaluateResponse8.Result.Should().Be("14");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "IntStaticProperty + 4", out var evaluateResponse9);
		evaluateResponse9.Result.Should().Be("14");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "ClassProperty.IntField + 4", out var evaluateResponse10);
		evaluateResponse10.Result.Should().Be("10");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "this.Get14() + 4", out var evaluateResponse11);
		evaluateResponse11.Result.Should().Be("18");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "MyClass.IntStaticProperty + 4", out var evaluateResponse12);
		evaluateResponse12.Result.Should().Be("14");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "DebuggableConsoleApp.MyClass.IntStaticProperty + 4", out var evaluateResponse13);
		evaluateResponse13.Result.Should().Be("14");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "Namespace1.AnotherClass.IntStaticProperty + 4", out var evaluateResponse14);
		evaluateResponse14.Result.Should().Be("14");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "this.DoubleNumber(4)", out var evaluateResponse15);
		evaluateResponse15.Result.Should().Be("8");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "this.DoubleNumber(4f)", out var evaluateResponse16);
		evaluateResponse16.Result.Should().Be("8");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "Get14()", out var evaluateResponse17);
		evaluateResponse17.Result.Should().Be("14");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "GetObject().IntProperty", out var chainedFuncEvalResponse);
		chainedFuncEvalResponse.Result.Should().Be("6");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "ReadObjectIn(GetObject())", out var referenceInTemporaryResponse);
		referenceInTemporaryResponse.Result.Should().Be("6");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "GetObjectByReference().IntProperty", out var referenceReturnResponse);
		referenceReturnResponse.Result.Should().Be("6");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "_classField.AddToIntField(ClearClassFieldAndCollect())", out var referenceAcrossFuncEvalResponse);
		referenceAcrossFuncEvalResponse.Result.Should().Be("6");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "WasClearedClassFieldReleased()", out var releasedEvaluationHandleResponse);
		releasedEvaluationHandleResponse.Result.Should().Be("true");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "new MyClass2().IntProperty", out var chainedFuncEvalResponse2);
		chainedFuncEvalResponse2.Result.Should().Be("6");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "IntProperty.ToString()", out var evaluateResponse18);
		evaluateResponse18.Result.Should().Be("\"10\"");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "this.TestMethod(4, \"asdf\")", out var evaluateResponse19);
		evaluateResponse19.Result.Should().Be("8");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "$\"Count = {IntProperty}\"", out var evaluateResponse20);
		evaluateResponse20.Result.Should().Be("\"Count = 10\"");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "this._classWithDebugDisplay", out var evaluateResponse21);
		evaluateResponse21.Result.Should().Be("IntProperty = 14");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "_classWithDebugDisplay", out var evaluateResponse22);
		evaluateResponse22.Result.Should().Be("IntProperty = 14");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "$\"{_instanceField}\"", out var evaluateResponse23);
		evaluateResponse23.Result.Should().Be("\"5\"");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "_intList", out var evaluateResponse24);
		evaluateResponse24.Result.Should().Be("Count = 4");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "$\"Count = {_intList.Count}\"", out var evaluateResponse25);
		evaluateResponse25.Result.Should().Be("\"Count = 4\"");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "_classWithDebugDisplay2", out var evaluateResponse26);
		evaluateResponse26.Result.Should().Be("Test = stringValue1");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "_classWithDebugDisplay3", out var evaluateResponse27);
		evaluateResponse27.Result.Should().Be("Test = stringValue2");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "myInt = 5", out var evaluateResponse28);
		evaluateResponse28.Result.Should().Be("5");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "myInt * 2", out var evaluateResponse29);
		evaluateResponse29.Result.Should().Be("10");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "enumVar", out var evaluateResponse30);
		evaluateResponse30.Result.Should().Be("SecondValue");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "enumVar = MyEnum.ThirdValue", out var evaluateResponse31);
		evaluateResponse31.Result.Should().Be("ThirdValue");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "GenericTypeWithStaticField<string>.IntValue", out var evaluateResponse32);
		evaluateResponse32.Result.Should().Be("4");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "GenericTypeWithStaticField<string>.IntProperty", out var evaluateResponse33);
		evaluateResponse33.Result.Should().Be("5");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "IntProperty = 16", out var evaluateResponse34);
		evaluateResponse34.Result.Should().Be("16");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "IntProperty", out var evaluateResponse35);
		evaluateResponse35.Result.Should().Be("16");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "Namespace1.AnotherClass.MyStaticMethod()", out var evaluateResponse36);
		evaluateResponse36.Result.Should().Be("Count = 1");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "Namespace1.AnotherClass.IntStaticProperty = 17", out var evaluateResponse37);
		evaluateResponse37.Result.Should().Be("17");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "Namespace1.AnotherClass.IntStaticProperty", out var evaluateResponse38);
		evaluateResponse38.Result.Should().Be("17");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "ClassProperty", out var evaluateResponse39);
		evaluateResponse39.Result.Should().Be("{DebuggableConsoleApp.MyClass2}");
		evaluateResponse39.VariablesReference.Should().BePositive();
		debugProtocolHost.WithVariablesRequest(evaluateResponse39.VariablesReference, out var variablesResponse39);
		variablesResponse39.Should().HaveCount(3);
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "structVar.ExtensionMethod()", out var evaluateResponse40);
		evaluateResponse40.Result.Should().Be("5");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "myInt == 5 && myInt < 10", out var evaluateResponse41);
		evaluateResponse41.Result.Should().Be("true");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "myInt == 4 && myInt < 10", out var evaluateResponse42);
		evaluateResponse42.Result.Should().Be("false");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "myInt == 4 || myInt < 10", out var evaluateResponse43);
		evaluateResponse43.Result.Should().Be("true");
		// Short-circuit: the RHS would throw a NullReferenceException if it were evaluated
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "myInt == 4 && nullableRefType.GetHashCode() > 0", out var evaluateResponse44);
		evaluateResponse44.Result.Should().Be("false");

		// Conversions must produce correctly-typed runtime values
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "enumVar = (MyEnum)2", out var evaluateResponse45);
		evaluateResponse45.Result.Should().Be("ThirdValue");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "myParam = 5", out var evaluateResponse46);
		evaluateResponse46.Result.Should().Be("5");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "(int)nullableIntWithVal + 1", out var evaluateResponse47);
		evaluateResponse47.Result.Should().Be("5");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "(int)5.5", out var evaluateResponse48);
		evaluateResponse48.Result.Should().Be("5");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "(int)enumVar", out var evaluateResponse49);
		evaluateResponse49.Result.Should().Be("2");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "nullableInt ?? 5", out var evaluateResponse50);
		evaluateResponse50.Result.Should().Be("5");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "nullableIntWithVal ?? 10", out var evaluateResponse51);
		evaluateResponse51.Result.Should().Be("4");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "(myInt > 3) == true", out var evaluateResponse52);
		evaluateResponse52.Result.Should().Be("true");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "(myInt > 5) == true", out var evaluateResponse53);
		evaluateResponse53.Result.Should().Be("false");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "myInt += 2", out var evaluateResponse54);
		evaluateResponse54.Result.Should().Be("7");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "GenericTypeWithStaticField<int[]>.IntValue", out var evaluateResponse55);
		evaluateResponse55.Result.Should().Be("4");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "GenericTypeWithStaticField<DateTime>.IntValue", out var corelibValueTypeGenericResponse);
		corelibValueTypeGenericResponse.Result.Should().Be("4");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "Array.Empty<DateTime>().Length", out var corelibValueTypeGenericMethodResponse);
		corelibValueTypeGenericMethodResponse.Result.Should().Be("0");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "new int[2].Length", out var intArrayResponse);
		intArrayResponse.Result.Should().Be("2");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "new MyClass[2].Length", out var classArrayResponse);
		classArrayResponse.Result.Should().Be("2");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "new MyClass[0].Length", out var classZeroLengthArrayResponse);
		classZeroLengthArrayResponse.Result.Should().Be("0");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "new DateTime[0].Length", out var corelibValueTypeZeroLengthArrayResponse);
		corelibValueTypeZeroLengthArrayResponse.Result.Should().Be("0");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "new DateTime[2].Length", out var corelibValueTypeArrayResponse);
		corelibValueTypeArrayResponse.Result.Should().Be("2");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "default(int)", out var evaluateResponse56);
		evaluateResponse56.Result.Should().Be("0");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "default(string)", out var defaultStringResponse);
		defaultStringResponse.Result.Should().Be("null");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "default(MyClass)", out var defaultRefTypeResponse);
		defaultRefTypeResponse.Result.Should().Be("null");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "default(int[])", out var defaultArrayResponse);
		defaultArrayResponse.Result.Should().Be("null");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "typeof(MyClass).ToString()", out var evaluateResponse57);
		evaluateResponse57.Result.Should().Be("\"DebuggableConsoleApp.MyClass\"");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "IncrementByRef(ref myInt)", out var evaluateResponse58);
		evaluateResponse58.Result.Should().Be("10");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "myInt", out var evaluateResponse59);
		evaluateResponse59.Result.Should().Be("10");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "AssignOut(out myInt)", out var evaluateResponse60);
		evaluateResponse60.Result.Should().Be("42");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "myInt", out var evaluateResponse61);
		evaluateResponse61.Result.Should().Be("42");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "AddOneIn(in myInt)", out var evaluateResponse62);
		evaluateResponse62.Result.Should().Be("43");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "_doubler(4)", out var evaluateResponse63);
		evaluateResponse63.Result.Should().Be("8");

		// User-defined operators are resolved from the bound tree (Roslyn), so value-type operands and operators
		// declared on the right operand's type work (structVar.Id == 5)
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "(structVar + structVar).Id", out var evaluateResponse64);
		evaluateResponse64.Result.Should().Be("10");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "(5 + structVar).Id", out var evaluateResponse65);
		evaluateResponse65.Result.Should().Be("10");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "(-structVar).Id", out var evaluateResponse66);
		evaluateResponse66.Result.Should().Be("-5");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "structVar == structVar", out var evaluateResponse67);
		evaluateResponse67.Result.Should().Be("true");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "this.DoubleNumber(myInt)", out var evaluateResponse68);
		evaluateResponse68.Result.Should().Be("84");

		debugProtocolHost.WithEvaluateRequest(stackFrameId, "nullableRefType == null", out var nullEqualityResponse);
		nullEqualityResponse.Result.Should().Be("true");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "this == this", out var referenceEqualityResponse);
		referenceEqualityResponse.Result.Should().Be("true");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "anotherVar == \"asdf\"", out var stringEqualityResponse);
		stringEqualityResponse.Result.Should().Be("true");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "nullableIntWithVal.HasValue == true", out var booleanEqualityResponse);
		booleanEqualityResponse.Result.Should().Be("true");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "enumVar == MyEnum.ThirdValue", out var equalEnumResponse);
		equalEnumResponse.Result.Should().Be("true");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "enumVar == MyEnum.SecondValue", out var unequalEnumResponse);
		unequalEnumResponse.Result.Should().Be("false");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "Convert.ToByte(myInt) == myInt", out var smallIntegralEqualityResponse);
		smallIntegralEqualityResponse.Result.Should().Be("true");

		debugProtocolHost.WithEvaluateRequest(stackFrameId, "(int?)myInt", out var nullableConversionResponse);
		nullableConversionResponse.Result.Should().Be("42");
		nullableConversionResponse.Type.Should().Be("int?");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "(MyEnum)(myInt - 40)", out var enumConversionResponse);
		enumConversionResponse.Result.Should().Be("ThirdValue");
		enumConversionResponse.Type.Should().Be("DebuggableConsoleApp.MyEnum");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "enumWithFlagsVar | MyEnumWithFlags.FlagValue2", out var enumOperatorResponse);
		enumOperatorResponse.Result.Should().Be("FlagValue1 | FlagValue2 | FlagValue3");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "(int)this.DoubleNumber(4f)", out var runtimeFloatToIntResponse);
		runtimeFloatToIntResponse.Result.Should().Be("8");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "(double)this.DoubleNumber(4f)", out var runtimeFloatToDoubleResponse);
		runtimeFloatToDoubleResponse.Result.Should().Be("8");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "this.DoubleNumber(this.DoubleNumber(4f) + 1f)", out var computedFloatArgumentResponse);
		computedFloatArgumentResponse.Result.Should().Be("18");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "unchecked((ulong)(uint)(myInt - 43))", out var unsignedWideningResponse);
		unsignedWideningResponse.Result.Should().Be("4294967295");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "this as object as string", out var incompatibleAsResponse);
		incompatibleAsResponse.Result.Should().Be("null");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "((string)(object)this).Length", out var incompatibleCastResponse);
		incompatibleCastResponse.Result.Should().Contain("InvalidCastException");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "(object)myInt != null", out var boxingResponse);
		boxingResponse.Result.Should().Be("true");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "(int)(object)myInt", out var unboxingResponse);
		unboxingResponse.Result.Should().Be("42");
		// unbox.any must reject a box whose runtime type does not match the target type
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "(long)(object)myInt", out var incompatibleUnboxResponse);
		incompatibleUnboxResponse.Result.Should().Contain("InvalidCastException");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "(int)(object)myParam", out var incompatibleUnboxResponse2);
		incompatibleUnboxResponse2.Result.Should().Contain("InvalidCastException");
		// reference type -> object is an identity conversion (no box), and must return the reference unchanged
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "(object)anotherVar", out var boxReferenceResponse);
		boxReferenceResponse.Result.Should().Be("\"asdf\"");
		// box/unbox round-trip of an exact enum type
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "(MyEnum)(object)enumVar", out var enumBoxResponse);
		enumBoxResponse.Result.Should().Be("ThirdValue");
		// This is technically not correct behaviour, ie doesn't match behaviour of actual code, but no other debugger handles it. Assert it.
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "(MyEnum)(object)2", out var enumBoxResponse2);
		enumBoxResponse2.Result.Should().Contain("InvalidCastException");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "Array.Empty<int>().Length", out var genericMethodResponse);
		genericMethodResponse.Result.Should().Be("0");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "int.Parse(anotherVar)", out var throwingMethodResponse);
		throwingMethodResponse.Result.Should().Contain("FormatException");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "checked(myInt + int.MaxValue)", out var checkedArithmeticResponse);
		checkedArithmeticResponse.Result.Should().Contain("OverflowException");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "myInt << (myInt - 10)", out var intShiftResponse);
		intShiftResponse.Result.Should().Be("42");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "-1 >>> (myInt - 41)", out var unsignedShiftResponse);
		unsignedShiftResponse.Result.Should().Be("2147483647");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "ulong.MaxValue / (ulong)(myInt - 40)", out var ulongDivisionResponse);
		ulongDivisionResponse.Result.Should().Be("9223372036854775807");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "0.0 / (myInt - myInt) == double.NaN", out var nanEqualityResponse);
		nanEqualityResponse.Result.Should().Be("false");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "TestMethod(myString: \"asdf\", myInt: 4)", out var namedArgumentsResponse);
		namedArgumentsResponse.Result.Should().Be("8");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "_intArray[0]", out var arrayIndexResponse);
		arrayIndexResponse.Result.Should().Be("2");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "_intList[0]", out var indexerResponse);
		indexerResponse.Result.Should().Be("1");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "nullableRefType?.GetHashCode()", out var conditionalAccessResponse);
		conditionalAccessResponse.Result.Should().Be("null");

		debugProtocolHost.WithEvaluateRequest(stackFrameId, "$\"{myInt:D5}\"", out var interpolationFormatResponse);
		interpolationFormatResponse.Result.Should().Be("\"00042\"");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "$\"{myInt,5}\"", out var interpolationAlignmentResponse);
		interpolationAlignmentResponse.Result.Should().Be("\"   42\"");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "$\"x{nullableRefType}y\"", out var nullInterpolationResponse);
		nullInterpolationResponse.Result.Should().Be("\"xy\"");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "$\"{ClassProperty}\"", out var objectInterpolationResponse);
		objectInterpolationResponse.Result.Should().Be("\"DebuggableConsoleApp.MyClass2\"");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "$\"{enumVar}\"", out var enumInterpolationResponse);
		enumInterpolationResponse.Result.Should().Be("\"ThirdValue\"");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "$\"{structVar}\"", out var structInterpolationResponse);
		structInterpolationResponse.Result.Should().Be("\"DebuggableConsoleApp.MyStruct\"");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "anotherVar + nullableRefType", out var nullConcatenationResponse);
		nullConcatenationResponse.Result.Should().Be("\"asdf\"");

		debugProtocolHost.WithEvaluateRequest(stackFrameId, "structVar.Id", out var structFieldReadResponse);
		structFieldReadResponse.Result.Should().Be("5");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "structVar.Id = 6", out var structFieldWriteResponse);
		structFieldWriteResponse.Result.Should().Be("6");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "structVar.Id", out var structFieldReadAfterWriteResponse);
		structFieldReadAfterWriteResponse.Result.Should().Be("6");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "structVar = default(MyStruct)", out var structAssignmentResponse);
		structAssignmentResponse.Result.Should().Be("{DebuggableConsoleApp.MyStruct}");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "structVar.Id", out var structFieldReadAfterAssignmentResponse);
		structFieldReadAfterAssignmentResponse.Result.Should().Be("0");

		// stobj: assigning a struct value to a field (stfld)
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "structVar.Id = 5", out var structResetIdResponse);
		structResetIdResponse.Result.Should().Be("5");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "_structField = structVar", out var structToFieldAssignmentResponse);
		structToFieldAssignmentResponse.Result.Should().Be("{DebuggableConsoleApp.MyStruct}");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "_structField.Id", out var structFieldValueReadBackResponse);
		structFieldValueReadBackResponse.Result.Should().Be("5");

		// stobj: struct value copied out of a field back into a local
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "structVar = _structField", out var structFromFieldAssignmentResponse);
		structFromFieldAssignmentResponse.Result.Should().Be("{DebuggableConsoleApp.MyStruct}");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "structVar.Id", out var structLocalValueAfterFieldCopyResponse);
		structLocalValueAfterFieldCopyResponse.Result.Should().Be("5");

		// ldobj: struct value loaded through a ref-return (call + ldobj + stloc)
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "structVar = GetStructByRef()", out var structViaRefReturnResponse);
		structViaRefReturnResponse.Result.Should().Be("{DebuggableConsoleApp.MyStruct}");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "structVar.Id", out var structIdViaRefReturnResponse);
		structIdViaRefReturnResponse.Result.Should().Be("5");

		// stobj: struct value stored through a ref-return (call + ldloc + stobj)
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "GetStructByRef() = structVar", out var structStoreViaRefReturnResponse);
		structStoreViaRefReturnResponse.Result.Should().Be("{DebuggableConsoleApp.MyStruct}");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "GetStructByRef().Id", out var structFieldIdAfterRefStoreResponse);
		structFieldIdAfterRefStoreResponse.Result.Should().Be("5");

		// unboxing conversion: cast-to-struct used as the receiver of an instance method call
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "((MyStruct)(object)structVar).GetId()", out var unboxReceiverResponse);
		unboxReceiverResponse.Result.Should().Be("5");

		debugProtocolHost.WithEvaluateRequest(stackFrameId, "default(MyStruct).Id", out var structDefaultResponse);
		structDefaultResponse.Result.Should().Be("0");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "sizeof(int)", out var sizeOfResponse);
		sizeOfResponse.Result.Should().Be("4");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "typeof(int[]).ToString()", out var arrayTypeOfResponse);
		arrayTypeOfResponse.Result.Should().Be("\"System.Int32[]\"");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "typeof(System.Collections.Generic.List<int>).ToString()", out var genericTypeOfResponse);
		genericTypeOfResponse.Result.Should().Be("\"System.Collections.Generic.List`1[System.Int32]\"");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "typeof(MyClassContainingAnotherClass.MyNestedClass).ToString()", out var nestedTypeOfResponse);
		nestedTypeOfResponse.Result.Should().Be("\"DebuggableConsoleApp.MyClassContainingAnotherClass+MyNestedClass\"");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "TryGetObject(out var objectResult) ? objectResult.IntProperty : 0", out var outVarResponse);
		outVarResponse.Result.Should().Be("6");

		debugProtocolHost.WithEvaluateRequest(stackFrameId, "new OverloadResolutionGeneric<int>().Pick(\"hello\")", out var genericTypeOverloadResponse);
		genericTypeOverloadResponse.Result.Should().Be("5");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "myInt = 42", out var myIntResetResponse);
		myIntResetResponse.Result.Should().Be("42");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "OverloadResolutionByRef.Pick(ref myInt)", out var byRefOverloadResponse);
		byRefOverloadResponse.Result.Should().Be("42");

		debugProtocolHost.WithEvaluateRequest(stackFrameId, "anotherVar = \"updated\"", out var referenceStringAssignmentResponse);
		referenceStringAssignmentResponse.Result.Should().Be("\"updated\"");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "anotherVar", out var referenceStringReadBackResponse);
		referenceStringReadBackResponse.Result.Should().Be("\"updated\"");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "anotherVar == \"updated\"", out var referenceStringEqualityResponse);
		referenceStringEqualityResponse.Result.Should().Be("true");

		// String reference identity: reading a debuggee string must preserve the original reference instead of
		// materializing a fresh string, so identity comparisons and assignments preserve aliasing.
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "IsSameString(anotherVar, anotherVar)", out var stringIdentityResponse);
		stringIdentityResponse.Result.Should().Be("true");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "object.ReferenceEquals(anotherVar, anotherVar)", out var stringReferenceEqualsResponse);
		stringReferenceEqualsResponse.Result.Should().Be("true");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "anotherVar = _name", out var stringAliasAssignmentResponse);
		stringAliasAssignmentResponse.Result.Should().Be("\"TestName\"");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "IsSameString(anotherVar, _name)", out var stringAliasIdentityResponse);
		stringAliasIdentityResponse.Result.Should().Be("true");

		debugProtocolHost.WithEvaluateRequest(stackFrameId, "nullableRefType = new MyClass()", out var referenceObjectAssignmentResponse);
		referenceObjectAssignmentResponse.Result.Should().Be("{DebuggableConsoleApp.MyClass}");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "nullableRefType == null", out var referenceObjectNotNullResponse);
		referenceObjectNotNullResponse.Result.Should().Be("false");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "nullableRefType = null", out var referenceNullAssignmentResponse);
		referenceNullAssignmentResponse.Result.Should().Be("null");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "nullableRefType == null", out var referenceNullReadBackResponse);
		referenceNullReadBackResponse.Result.Should().Be("true");

		debugProtocolHost.WithEvaluateRequest(stackFrameId, "_classField = null", out var referenceFieldNullAssignmentResponse);
		referenceFieldNullAssignmentResponse.Result.Should().Be("null");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "_classField == null", out var referenceFieldNullReadBackResponse);
		referenceFieldNullReadBackResponse.Result.Should().Be("true");

		debugProtocolHost.WithEvaluateRequest(stackFrameId, "(new string[1])[0] = \"array-string\"", out var arrayStringAssignmentResponse);
		arrayStringAssignmentResponse.Result.Should().Be("\"array-string\"");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "(new MyClass2[1])[0] = null", out var arrayNullAssignmentResponse);
		arrayNullAssignmentResponse.Result.Should().Be("null");
	}

	[Fact]
	public async Task SharpDbgCli_EvaluationRequest_ShadowedLocal_ResolvesCorrectSlot()
	{
		var startSuspended = true;

		var (debugProtocolHost, initializedEventTcs, debugEventTcs, adapter, p2) = TestHelper.GetRunningDebugProtocolHostInProc(testOutputHelper, startSuspended);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(p2.Id)
			.WaitForInitializedEvent(initializedEventTcs);
		debugProtocolHost
			.WithBreakpointsRequest(48, Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "Lambdas", "MyLambdaClass.cs"))
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(debugEventTcs);
		debugProtocolHost
			.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTraceResponse)
			.WithScopesRequest(stackTraceResponse.StackFrames!.First().Id, out var scopesResponse);

		var stackFrameId = stackTraceResponse.StackFrames!.First().Id;

		// 'value' must resolve to the innermost lambda's parameter (200), not the hoisted method-local 'value' (100)
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "value", out var evaluateResponse);
		evaluateResponse.Result.Should().Be("200");
	}

	[Fact]
	public async Task SharpDbgCli_EvaluationRequest_GenericMethod_Evaluates()
	{
		var startSuspended = true;

		var (debugProtocolHost, initializedEventTcs, debugEventTcs, adapter, p2) = TestHelper.GetRunningDebugProtocolHostInProc(testOutputHelper, startSuspended);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(p2.Id)
			.WaitForInitializedEvent(initializedEventTcs);
		debugProtocolHost
			.WithBreakpointsRequest(7, Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "MyClassWithGenericMethod.cs"))
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(debugEventTcs);
		debugProtocolHost
			.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTraceResponse)
			.WithScopesRequest(stackTraceResponse.StackFrames!.First().Id, out var scopesResponse);

		var stackFrameId = stackTraceResponse.StackFrames!.First().Id;

		debugProtocolHost.WithEvaluateRequest(stackFrameId, "default(T)", out var evaluateResponse);
		evaluateResponse.Result.Should().Be("0");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "arg", out var evaluateResponse3);
		evaluateResponse3.Result.Should().Be("4");
		debugProtocolHost.WithEvaluateRequest(stackFrameId, "typeof(T)", out var evaluateResponse4);
		evaluateResponse4.Result.Should().Be("System.Int32");
	}
}
