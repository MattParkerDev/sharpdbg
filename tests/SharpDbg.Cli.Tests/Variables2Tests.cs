using AwesomeAssertions;
using AwesomeAssertions.Execution;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using SharpDbg.Cli.Tests.Helpers;

namespace SharpDbg.Cli.Tests;

public class Variables2Tests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task SyncMethod_VariablesClass_VariablesRequest_ReturnsCorrectVariables()
	{
		var startSuspended = true;

		var (debugProtocolHost, initializedEventTcs, debugEventTcs, adapter, p2) = TestHelper.GetRunningDebugProtocolHost(testOutputHelper, startSuspended);
		using var assertionScope = new AssertionScope();
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(p2.Id)
			.WaitForInitializedEvent(initializedEventTcs);
		var breakpointedFilePath = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "VariablesClass.cs");
		debugProtocolHost
			.WithBreakpointsRequest([150], breakpointedFilePath)
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(debugEventTcs);
		stoppedEvent.ReadStopInfo().Should().Be((breakpointedFilePath, 150, 3));
		debugProtocolHost
			.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTraceResponse)
			.WithScopesRequest(stackTraceResponse.StackFrames!.First().Id, out var scopesResponse);

		scopesResponse.Scopes.Should().HaveCount(1);
		var scope = scopesResponse.Scopes.Single();

		var expectedDateTimeString = new DateTime(2026, 6, 13, 5, 42, 39).ToString();
		List<Variable> expectedVariables =
		[
			new() { VariablesReference = 2,  Name = "this",						EvaluateName = "this",                 		Value = "{DebuggableConsoleApp.VariablesClass}",	Type = "DebuggableConsoleApp.VariablesClass" },
			new() { VariablesReference = 0,  Name = "localBool",				EvaluateName = "localBool",            		Value = "true",										Type = "bool" },
			new() { VariablesReference = 0,  Name = "localByte",				EvaluateName = "localByte",            		Value = "1",										Type = "byte" },
			new() { VariablesReference = 0,  Name = "localSByte",				EvaluateName = "localSByte",           		Value = "-1",										Type = "sbyte" },
			new() { VariablesReference = 0,  Name = "localShort",           	EvaluateName = "localShort",           		Value = "-2",										Type = "short" },
			new() { VariablesReference = 0,  Name = "localUShort",          	EvaluateName = "localUShort",          		Value = "2",										Type = "ushort" },
			new() { VariablesReference = 0,  Name = "localInt",             	EvaluateName = "localInt",             		Value = "3",										Type = "int" },
			new() { VariablesReference = 0,  Name = "localUInt",            	EvaluateName = "localUInt",            		Value = "4",										Type = "uint" },
			new() { VariablesReference = 0,  Name = "localLong",            	EvaluateName = "localLong",            		Value = "5",										Type = "long" },
			new() { VariablesReference = 0,  Name = "localULong",           	EvaluateName = "localULong",           		Value = "6",										Type = "ulong" },
			new() { VariablesReference = 0,  Name = "localChar",            	EvaluateName = "localChar",            		Value = "90 'Z'",									Type = "char" },
			new() { VariablesReference = 0,  Name = "localFloat",           	EvaluateName = "localFloat",           		Value = "1.5",										Type = "float" },
			new() { VariablesReference = 0,  Name = "localDouble",          	EvaluateName = "localDouble",		   		Value = "2.5",										Type = "double" },
			new() { VariablesReference = 0,  Name = "localDecimal",         	EvaluateName = "localDecimal",         		Value = "3.5",										Type = "decimal" },
			new() { VariablesReference = 0,  Name = "localNullableInt",     	EvaluateName = "localNullableInt",			Value = "123",										Type = "int?" },
			new() { VariablesReference = 0,  Name = "localNullableIntNull", 	EvaluateName = "localNullableIntNull",		Value = "null",										Type = "int?" },
			new() { VariablesReference = 0,  Name = "localNullableDecimal", 	EvaluateName = "localNullableDecimal",		Value = "2.5",										Type = "decimal?" },
			new() { VariablesReference = 0,  Name = "localNullableDecimalNull",	EvaluateName = "localNullableDecimalNull",	Value = "null",										Type = "decimal?" },
			new() { VariablesReference = 0,  Name = "localString",          	EvaluateName = "localString",          		Value = "\"hello\"",								Type = "string" },
			new() { VariablesReference = 0,  Name = "localNullableString",  	EvaluateName = "localNullableString",  		Value = "null",										Type = "string" },
			new() { VariablesReference = 3,  Name = "localObject",          	EvaluateName = "localObject",          		Value = "{object}",									Type = "object" },
			new() { VariablesReference = 0,  Name = "localNullableObject",  	EvaluateName = "localNullableObject",  		Value = "null",										Type = "object" },
			new() { VariablesReference = 0,  Name = "localBoxedInt",  			EvaluateName = "localBoxedInt",  			Value = "42",										Type = "int" },
			new() { VariablesReference = 4,  Name = "localArray",           	EvaluateName = "localArray",           		Value = "int[3]",									Type = "int[]" },
			new() { VariablesReference = 5,  Name = "localList",            	EvaluateName = "localList",            		Value = "Count = 2",								Type = "System.Collections.Generic.List<string>" },
			new() { VariablesReference = 6,  Name = "localDictionary",      	EvaluateName = "localDictionary",      		Value = "Count = 1",								Type = "System.Collections.Generic.Dictionary<int, string>" },
			new() { VariablesReference = 7,  Name = "localStruct",          	EvaluateName = "localStruct",          		Value = "{DebuggableConsoleApp.TestStruct}",		Type = "DebuggableConsoleApp.TestStruct" },
			new() { VariablesReference = 8,  Name = "localClass",           	EvaluateName = "localClass",           		Value = "{DebuggableConsoleApp.TestClass}",			Type = "DebuggableConsoleApp.TestClass" },
			new() { VariablesReference = 9,  Name = "localRecord",          	EvaluateName = "localRecord",          		Value = "TestRecord { Name = record, Age = 1 }",	Type = "DebuggableConsoleApp.TestRecord" },
			new() { VariablesReference = 10, Name = "localInterface",       	EvaluateName = "localInterface",       		Value = "{DebuggableConsoleApp.TestClass}",			Type = "DebuggableConsoleApp.TestClass" },
			new() { VariablesReference = 11, Name = "localDelegate",        	EvaluateName = "localDelegate",        		Value = "{System.Func<int, int>}",					Type = "System.Func<int, int>" },
			new() { VariablesReference = 12, Name = "localTuple",           	EvaluateName = "localTuple",           		Value = "(1, \"stringInTuple\")",                 		Type = "System.Tuple<int, string>" },
			new() { VariablesReference = 13, Name = "localValueTuple",      	EvaluateName = "localValueTuple",      		Value = "(2, \"stringInValueTuple\")",					Type = "System.ValueTuple<int, string>" },
			new() { VariablesReference = 14, Name = "localGeneric",         	EvaluateName = "localGeneric",         		Value = "{DebuggableConsoleApp.GenericBox<int>}",	Type = "DebuggableConsoleApp.GenericBox<int>" },
			new() { VariablesReference = 0,  Name = "localDynamic",         	EvaluateName = "localDynamic",         		Value = "241",                                  	Type = "int" },
			new() { VariablesReference = 15, Name = "localAnonymous",       	EvaluateName = "localAnonymous",       		Value = "{ Id = 1, Name = \"Anonymous\" }",				Type = "<>f__AnonymousType0<int, string>" },
			new() { VariablesReference = 16, Name = "localDateTime",        	EvaluateName = "localDateTime",        		Value = expectedDateTimeString,						Type = "System.DateTime" },
			new() { VariablesReference = 17, Name = "localGuid",            	EvaluateName = "localGuid",            		Value = "27de5b68-af24-4e59-a785-dde52e2ea7af",		Type = "System.Guid" },
			new() { VariablesReference = 18, Name = "localCompositeValues", EvaluateName = "localCompositeValues", Value = "{DebuggableConsoleApp.CompositeValueFixtures}", Type = "DebuggableConsoleApp.CompositeValueFixtures" },
		];

		debugProtocolHost.WithVariablesRequest(scope.VariablesReference, out var variables);

		variables.Should().HaveCount(39);
		variables.ShouldBeEquivalentToDebuggerVariables(expectedVariables);
		debugProtocolHost.AssertInstanceThisInstanceVariables(variables.Single(s => s.Name == "this").VariablesReference, breakpointedFilePath);
		debugProtocolHost.AssertCompositeValueVariables(variables.Single(s => s.Name == "localCompositeValues").VariablesReference);
	}
}

file static class TestExtensions
{
	public static void AssertInstanceThisInstanceVariables(this DebugProtocolHost debugProtocolHost, int variablesReference, string breakpointedFilePath)
	{
		var expectedDateTimeField = new DateTime(2026, 6, 15, 10, 5, 8).ToString();
		var expectedDateOnlyField = new DateOnly(2026, 6, 15).ToString();
		var expectedTimeOnlyField = new TimeOnly(10, 2, 16).ToString();
		var expectedTimeSpanField = TimeSpan.FromMinutes(5).ToString();
		var expectedGuidField = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
		var expectedNullableGuidField= "f0e1d2c3-b4a5-9687-7869-5a4b3c2d1e0f";
		var expectedThrowingPropertyValue = $"System.InvalidOperationException: ThrowingProperty was accessed{Environment.NewLine}   at DebuggableConsoleApp.VariablesClass.get_ThrowingProperty() in {breakpointedFilePath}:line 93";

		List<Variable> expectedVariables =
		[
			new() { VariablesReference =  0,  Name = "BoolField",              	    EvaluateName = "BoolField",              	  Value = "true",                                                      Type = "bool" },
			new() { VariablesReference =  0,  Name = "ByteField",              	    EvaluateName = "ByteField",              	  Value = "1",                                                         Type = "byte" },
			new() { VariablesReference =  0,  Name = "SByteField",             	    EvaluateName = "SByteField",             	  Value = "-1",                                                        Type = "sbyte" },
			new() { VariablesReference =  0,  Name = "ShortField",             	    EvaluateName = "ShortField",             	  Value = "-2",                                                        Type = "short" },
			new() { VariablesReference =  0,  Name = "UShortField",            	    EvaluateName = "UShortField",            	  Value = "2",                                                         Type = "ushort" },
			new() { VariablesReference =  0,  Name = "IntField",               	    EvaluateName = "IntField",               	  Value = "123",                                                       Type = "int" },
			new() { VariablesReference =  0,  Name = "UIntField",              	    EvaluateName = "UIntField",              	  Value = "123",                                                       Type = "uint" },
			new() { VariablesReference =  0,  Name = "LongField",              	    EvaluateName = "LongField",              	  Value = "123456789",                                                 Type = "long" },
			new() { VariablesReference =  0,  Name = "ULongField",             	    EvaluateName = "ULongField",             	  Value = "123456789",                                                 Type = "ulong" },
			new() { VariablesReference =  0,  Name = "CharField",              	    EvaluateName = "CharField",              	  Value = "65 'A'",                                                    Type = "char" },
			new() { VariablesReference =  0,  Name = "FloatField",             	    EvaluateName = "FloatField",             	  Value = "1.23",                                                      Type = "float" },
			new() { VariablesReference =  0,  Name = "DoubleField",            	    EvaluateName = "DoubleField",            	  Value = "2.34",                                                      Type = "double" },
			new() { VariablesReference =  0,  Name = "DecimalField",           	    EvaluateName = "DecimalField",           	  Value = "3.45",                                                      Type = "decimal" },
			new() { VariablesReference =  0,  Name = "NullableIntField",       	    EvaluateName = "NullableIntField",       	  Value = "42",                                                        Type = "int?" },
			new() { VariablesReference =  0,  Name = "NullableIntNullField",   	    EvaluateName = "NullableIntNullField",   	  Value = "null",                                                      Type = "int?" },
			new() { VariablesReference =  0,  Name = "NullableBoolField",      	    EvaluateName = "NullableBoolField",      	  Value = "true",                                                      Type = "bool?" },
			new() { VariablesReference =  0,  Name = "NullableBoolNullField",  	    EvaluateName = "NullableBoolNullField",  	  Value = "null",                                                      Type = "bool?" },
			new() { VariablesReference = 19,  Name = "NullableGuidField",      	    EvaluateName = "NullableGuidField",      	  Value = expectedNullableGuidField,                                   Type = "System.Guid?" },
			new() { VariablesReference = 20,  Name = "NullableEnumField",      	    EvaluateName = "NullableEnumField",      	  Value = "Friday",                                                    Type = "System.DayOfWeek?" },
			new() { VariablesReference =  0,  Name = "NullableEnumNullField",  	    EvaluateName = "NullableEnumNullField",  	  Value = "null",                                                      Type = "System.DayOfWeek?" },
			new() { VariablesReference =  0,  Name = "StringField",            	    EvaluateName = "StringField",            	  Value = "\"Hello\"",                                                 Type = "string" },
			new() { VariablesReference =  0,  Name = "NullableStringField",    	    EvaluateName = "NullableStringField",    	  Value = "null",                                                      Type = "string" },
			new() { VariablesReference = 21,  Name = "ObjectField",            	    EvaluateName = "ObjectField",            	  Value = "{object}",                                                  Type = "object" },
			new() { VariablesReference =  0,  Name = "NullableObjectField",    	    EvaluateName = "NullableObjectField",    	  Value = "null",                                                      Type = "object" },
			new() { VariablesReference = 22,  Name = "IntArrayField",          	    EvaluateName = "IntArrayField",          	  Value = "int[3]",                                                    Type = "int[]" },
			new() { VariablesReference = 23,  Name = "NullableStringArrayField",    EvaluateName = "NullableStringArrayField",	  Value = "string[3]",									               Type = "string[]" },
			new() { VariablesReference = 24,  Name = "MultiDimArrayField",     	    EvaluateName = "MultiDimArrayField",     	  Value = "int[2, 5]",                                                 Type = "int[,]" },
			new() { VariablesReference = 25,  Name = "JaggedArrayField",       	    EvaluateName = "JaggedArrayField",       	  Value = "int[2][]",                                                  Type = "int[][]" },
			new() { VariablesReference = 26,  Name = "ListField",              	    EvaluateName = "ListField",              	  Value = "Count = 3",                                                 Type = "System.Collections.Generic.List<int>" },
			new() { VariablesReference = 27,  Name = "DictionaryField",        	    EvaluateName = "DictionaryField",        	  Value = "Count = 2",                                                 Type = "System.Collections.Generic.Dictionary<string, int>" },
			new() { VariablesReference = 28,  Name = "DateTimeField",          	    EvaluateName = "DateTimeField",          	  Value = expectedDateTimeField,                                       Type = "System.DateTime" },
			new() { VariablesReference = 29,  Name = "DateOnlyField",          	    EvaluateName = "DateOnlyField",          	  Value = expectedDateOnlyField,                                       Type = "System.DateOnly" },
			new() { VariablesReference = 30,  Name = "TimeOnlyField",          	    EvaluateName = "TimeOnlyField",          	  Value = expectedTimeOnlyField,                                       Type = "System.TimeOnly" },
			new() { VariablesReference = 31,  Name = "TimeSpanField",          	    EvaluateName = "TimeSpanField",          	  Value = expectedTimeSpanField,                                       Type = "System.TimeSpan" },
			new() { VariablesReference = 32,  Name = "GuidField",              	    EvaluateName = "GuidField",              	  Value = expectedGuidField,                                           Type = "System.Guid" },
			new() { VariablesReference = 33,  Name = "EnumField",              	    EvaluateName = "EnumField",              	  Value = "Monday",                                                    Type = "System.DayOfWeek" },
			new() { VariablesReference = 34,  Name = "StructField",            	    EvaluateName = "StructField",            	  Value = "{DebuggableConsoleApp.TestStruct}",                         Type = "DebuggableConsoleApp.TestStruct" },
			new() { VariablesReference = 35,  Name = "ClassField",             	    EvaluateName = "ClassField",             	  Value = "{DebuggableConsoleApp.TestClass}",                          Type = "DebuggableConsoleApp.TestClass" },
			new() { VariablesReference = 36,  Name = "RecordField",            	    EvaluateName = "RecordField",            	  Value = "TestRecord { Name = Alice, Age = 42 }",                     Type = "DebuggableConsoleApp.TestRecord" },
			new() { VariablesReference = 37,  Name = "RecordStructField",      	    EvaluateName = "RecordStructField",      	  Value = "TestRecordStruct { Value = 7 }",                            Type = "DebuggableConsoleApp.TestRecordStruct" },
			new() { VariablesReference = 38,  Name = "InterfaceField",         	    EvaluateName = "InterfaceField",         	  Value = "{DebuggableConsoleApp.TestClass}",                          Type = "DebuggableConsoleApp.TestClass" },
			new() { VariablesReference = 39,  Name = "DelegateField",          	    EvaluateName = "DelegateField",          	  Value = "{System.Func<int, int>}",                                   Type = "System.Func<int, int>" },
			new() { VariablesReference = 40,  Name = "TupleField",             	    EvaluateName = "TupleField",             	  Value = "(1, \"tuple\")",                                                Type = "System.Tuple<int, string>" },
			new() { VariablesReference = 41,  Name = "ValueTupleField",        	    EvaluateName = "ValueTupleField",        	  Value = "(123, \"value tuple\")",                                        Type = "System.ValueTuple<int, string>" },
			new() { VariablesReference = 42,  Name = "GenericField",           	    EvaluateName = "GenericField",           	  Value = "{DebuggableConsoleApp.GenericBox<string>}",                 Type = "DebuggableConsoleApp.GenericBox<string>" },
			new() { VariablesReference =  0,  Name = "DynamicField",           	    EvaluateName = "DynamicField",           	  Value = "\"dynamic value\"",                                         Type = "string" },
			new() { VariablesReference =  0,  Name = "ReadonlyField",          	    EvaluateName = "ReadonlyField",          	  Value = "\"readonly\"",                                              Type = "string" },
			new() { VariablesReference =  0,  Name = "IntProperty",            	    EvaluateName = "IntProperty",            	  Value = "100",                                                       Type = "int" },
			new() { VariablesReference =  0,  Name = "NullableStringProperty", 	    EvaluateName = "NullableStringProperty", 	  Value = "null",                                                      Type = "string" },
			new() { VariablesReference = 43,  Name = "_genericTypeWithStaticField", EvaluateName = "_genericTypeWithStaticField", Value = "{DebuggableConsoleApp.GenericTypeWithStaticField<string>}", Type = "DebuggableConsoleApp.GenericTypeWithStaticField<string>" },
			new() { VariablesReference =  0,  Name = "_stringFieldWithNewLine",     EvaluateName = "_stringFieldWithNewLine",     Value = """ "Test\nValue\\n\"quoted\"\r\t" """.Trim(),               Type = "string" },
			new() { VariablesReference = 44,  Name = "ClassProperty",          	    EvaluateName = "ClassProperty",          	  Value = "{DebuggableConsoleApp.TestClass}",                          Type = "DebuggableConsoleApp.TestClass" },
			new() { VariablesReference = 45,  Name = "RecordProperty",         	    EvaluateName = "RecordProperty",         	  Value = "TestRecord { Name = InitProperty, Age = 5 }",	           Type = "DebuggableConsoleApp.TestRecord" },
			new() { VariablesReference =  0,  Name = "ComputedProperty",       	    EvaluateName = "ComputedProperty",       	  Value = "246",                                                       Type = "int" },
			// TODO: Type should be int
			new() { VariablesReference = 46,  Name = "ThrowingProperty",       	    EvaluateName = "ThrowingProperty",       	  Value = expectedThrowingPropertyValue,                               Type = "System.InvalidOperationException" },
			new() { VariablesReference = 47,  Name = "Static members",			    EvaluateName = "Static members",			  Value = "",												           Type = "", PresentationHint = new VariablePresentationHint { Kind = VariablePresentationHint.KindValue.Class } },
		];
		debugProtocolHost.WithVariablesRequest(variablesReference, out var thisInstanceVariables);
		thisInstanceVariables.Should().HaveCount(expectedVariables.Count);
		thisInstanceVariables.ShouldBeEquivalentToDebuggerVariables(expectedVariables);
		debugProtocolHost.AssertStaticFieldsOnGenericType(thisInstanceVariables.Single(s => s.Name == "_genericTypeWithStaticField").VariablesReference);
		debugProtocolHost.AssertInstanceThisStaticVariables(thisInstanceVariables.Single(s => s.Name == "Static members").VariablesReference);
		debugProtocolHost.AssertMultiDimArrayVariables(thisInstanceVariables.Single(s => s.Name == "MultiDimArrayField").VariablesReference);
	}

	private static readonly VariablePresentationHint _multiDimArrayRowPresentationHint = new() { Kind = VariablePresentationHint.KindValue.Class };
	private static void AssertMultiDimArrayVariables(this DebugProtocolHost debugProtocolHost, int variablesReference)
	{
		List<Variable> expectedVariables =
		[
			new() { VariablesReference =  53, Name = "[0, ...]",    EvaluateName = "[0, ...]",    Value = "",    Type = "", PresentationHint = _multiDimArrayRowPresentationHint },
			new() { VariablesReference =  54, Name = "[1, ...]",    EvaluateName = "[1, ...]",    Value = "",    Type = "", PresentationHint = _multiDimArrayRowPresentationHint },
		];
		debugProtocolHost.WithVariablesRequest(variablesReference, out var multiDimArrayVariables);
		multiDimArrayVariables.Should().HaveCount(expectedVariables.Count);
		multiDimArrayVariables.ShouldBeEquivalentToDebuggerVariables(expectedVariables);

		List<Variable> expectedVariables2 =
		[
			new() { VariablesReference =  0, Name = "[0, 0]",    EvaluateName = "[0, 0]",    Value = "1",    Type = "int", PresentationHint = _arrayElementPresentationHint },
			new() { VariablesReference =  0, Name = "[0, 1]",    EvaluateName = "[0, 1]",    Value = "2",    Type = "int", PresentationHint = _arrayElementPresentationHint },
			new() { VariablesReference =  0, Name = "[0, 2]",    EvaluateName = "[0, 2]",    Value = "3",    Type = "int", PresentationHint = _arrayElementPresentationHint },
			new() { VariablesReference =  0, Name = "[0, 3]",    EvaluateName = "[0, 3]",    Value = "4",    Type = "int", PresentationHint = _arrayElementPresentationHint },
			new() { VariablesReference =  0, Name = "[0, 4]",    EvaluateName = "[0, 4]",    Value = "5",    Type = "int", PresentationHint = _arrayElementPresentationHint },
		];

		debugProtocolHost.WithVariablesRequest(multiDimArrayVariables[0].VariablesReference, out var multiDimArrayRankVariables);
		multiDimArrayRankVariables.Should().HaveCount(expectedVariables2.Count);
		multiDimArrayRankVariables.ShouldBeEquivalentToDebuggerVariables(expectedVariables2);
	}

	private static void AssertStaticFieldsOnGenericType(this DebugProtocolHost debugProtocolHost, int variablesReference)
	{
		debugProtocolHost.WithVariablesRequest(variablesReference, out var classVariables);
		var staticMembersVariablesReference = classVariables.Single().VariablesReference;
		List<Variable> expectedVariables =
		[
			new() { VariablesReference =  0, Name = "IntValue",    EvaluateName = "IntValue",    Value = "4",    Type = "int" },
			new() { VariablesReference =  0, Name = "Value",       EvaluateName = "Value",       Value = "null", Type = "string" },
			new() { VariablesReference =  0, Name = "IntProperty", EvaluateName = "IntProperty", Value = "5",    Type = "int" },
		];
		debugProtocolHost.WithVariablesRequest(staticMembersVariablesReference, out var staticFieldsOnGenericType);
		staticFieldsOnGenericType.Should().HaveCount(expectedVariables.Count);
		staticFieldsOnGenericType.ShouldBeEquivalentToDebuggerVariables(expectedVariables);
	}

	private static void AssertInstanceThisStaticVariables(this DebugProtocolHost debugProtocolHost, int variablesReference)
	{
		debugProtocolHost.WithVariablesRequest(variablesReference, out var staticMemberVariables);
		List<Variable> expectedVariables =
		[
			new() { VariablesReference =  0, Name = "StaticField", EvaluateName = "StaticField", Value = "999", Type = "int" },
			new() { VariablesReference = 49, Name = "StaticEnumerableField", EvaluateName = "StaticEnumerableField", Value = "Count = 4", Type = "System.Linq.Enumerable.RangeIterator<int>" },
			new() { VariablesReference =  0, Name = "ConstField",  EvaluateName = "ConstField",  Value = "\"const\"", Type = "string" },
			new() { VariablesReference =  0, Name = "ConstFieldNullString",  EvaluateName = "ConstFieldNullString",  Value = "null", Type = "string" },
			new() { VariablesReference =  0, Name = "ConstFieldBool",  EvaluateName = "ConstFieldBool",  Value = "true", Type = "bool" },
			new() { VariablesReference =  0, Name = "ConstFieldDecimal",  EvaluateName = "ConstFieldDecimal",  Value = "4.5", Type = "decimal" },
			new() { VariablesReference =  0, Name = "ConstFieldChar",  EvaluateName = "ConstFieldChar",  Value = "68 'D'", Type = "char" },
			new() { VariablesReference =  0, Name = "ConstFieldEnum",  EvaluateName = "ConstFieldEnum",  Value = "SecondValue", Type = "DebuggableConsoleApp.MyEnum" },
			new() { VariablesReference =  0, Name = "ConstFieldFlagsEnum",  EvaluateName = "ConstFieldFlagsEnum",  Value = "FlagValue1 | FlagValue3", Type = "DebuggableConsoleApp.MyEnumWithFlags" },
			new() { VariablesReference = 50, Name = "ConstFieldByteArraySpan",  EvaluateName = "ConstFieldByteArraySpan",  Value = "System.ReadOnlySpan<Byte>[4]", Type = "System.ReadOnlySpan<byte>" },
		];
		staticMemberVariables.Should().HaveCount(expectedVariables.Count);
		staticMemberVariables.ShouldBeEquivalentToDebuggerVariables(expectedVariables);
		debugProtocolHost.AssertIEnumerableMembers(staticMemberVariables.Single(s => s.Name == "StaticEnumerableField").VariablesReference);
	}

	private static readonly VariablePresentationHint _arrayElementPresentationHint = new() { Kind = VariablePresentationHint.KindValue.Data };
	private static void AssertIEnumerableMembers(this DebugProtocolHost debugProtocolHost, int variablesReference)
	{
		debugProtocolHost.WithVariablesRequest(variablesReference, out var enumerableMembers);
		List<Variable> expectedVariables =
		[
			new() { VariablesReference = 51, Name = "Raw View", EvaluateName = "Raw View", Value = "", Type = "", PresentationHint = new VariablePresentationHint { Kind = VariablePresentationHint.KindValue.Class } },
			new() { VariablesReference = 52, Name = "Results", EvaluateName = "Results", Value = "Expanding will force enumeration of the object", Type = "", PresentationHint = new VariablePresentationHint { Kind = VariablePresentationHint.KindValue.Class } },
		];
		enumerableMembers.Should().HaveCount(expectedVariables.Count);
		enumerableMembers.ShouldBeEquivalentToDebuggerVariables(expectedVariables);

		List<Variable> expectedVariables2 =
		[
			new() { VariablesReference =  0, Name = "[0]", EvaluateName = "[0]", Value = "1", Type = "int", PresentationHint = _arrayElementPresentationHint },
			new() { VariablesReference =  0, Name = "[1]", EvaluateName = "[1]", Value = "2", Type = "int", PresentationHint = _arrayElementPresentationHint },
			new() { VariablesReference =  0, Name = "[2]", EvaluateName = "[2]", Value = "3", Type = "int", PresentationHint = _arrayElementPresentationHint },
			new() { VariablesReference =  0, Name = "[3]", EvaluateName = "[3]", Value = "4", Type = "int", PresentationHint = _arrayElementPresentationHint },
		];

		debugProtocolHost.WithVariablesRequest(enumerableMembers.Single(s => s.Name == "Results").VariablesReference, out var enumerableResultsMembers);
		enumerableResultsMembers.Should().HaveCount(expectedVariables2.Count);
		enumerableResultsMembers.ShouldBeEquivalentToDebuggerVariables(expectedVariables2);
	}

	public static void AssertCompositeValueVariables(this DebugProtocolHost debugProtocolHost, int variablesReference)
	{
		List<Variable> expectedVariables =
		[
			new() { VariablesReference = 55, Name = "NullAndEmptyStrings", EvaluateName = "NullAndEmptyStrings", Value = "(1, null, \"\", \"null\")", Type = "System.Tuple<int, string, string, string>" },
			new() { VariablesReference = 56, Name = "EscapedString",       EvaluateName = "EscapedString",       Value = "(1, \"a\\n\\\"b\\\\c\")", Type = "System.ValueTuple<int, string>" },
			new() { VariablesReference = 57, Name = "NestedTuple",         EvaluateName = "NestedTuple",         Value = "(1, (2, \"nested\"))", Type = "System.ValueTuple<int, System.ValueTuple<int, string>>" },
			new() { VariablesReference = 58, Name = "LongValueTuple",      EvaluateName = "LongValueTuple",      Value = "(1, 2, 3, 4, 5, 6, 7, \"eight\", \"nine\")", Type = "System.ValueTuple<int, int, int, int, int, int, int, System.ValueTuple<string, string>>" },
			new() { VariablesReference = 59, Name = "LongTuple",           EvaluateName = "LongTuple",           Value = "(1, 2, 3, 4, 5, 6, 7, \"eight\")", Type = "System.Tuple<int, int, int, int, int, int, int, System.Tuple<string>>" },
			new() { VariablesReference = 60, Name = "EmptyTuple",          EvaluateName = "EmptyTuple",          Value = "()", Type = "System.ValueTuple" },
			new() { VariablesReference = 61, Name = "Anonymous",           EvaluateName = "Anonymous",           Value = "{ Text = \"a\\n\\\"b\\\\c\", Empty = \"\", Missing = null, Nested = (1, \"nested\") }", Type = "<>f__AnonymousType1<string, string, string, System.ValueTuple<int, string>>" },
			new() { VariablesReference = 62, Name = "FailedMember",        EvaluateName = "FailedMember",        Value = "(error: The name 'DoesNotExist' does not exist in the current context, 2)", Type = "System.Tuple<DebuggableConsoleApp.InvalidDebuggerDisplay, int>", PresentationHint = new VariablePresentationHint { Attributes = VariablePresentationHint.AttributesValue.FailedEvaluation } },
			new() { VariablesReference = 63, Name = "NullableTuple",       EvaluateName = "NullableTuple",       Value = "(4, \"nullable\")", Type = "System.ValueTuple<int, string>?" },
			new() { VariablesReference = 0,  Name = "NullTuple",           EvaluateName = "NullTuple",           Value = "null", Type = "System.ValueTuple<int, string>?" },
			new() { VariablesReference = 64, Name = "NullableDebuggerDisplay", EvaluateName = "NullableDebuggerDisplay", Value = "display: 5", Type = "DebuggableConsoleApp.NullableDebuggerDisplay?" },
			new() { VariablesReference = 65, Name = "NullableToString",    EvaluateName = "NullableToString",    Value = "to-string: 6", Type = "DebuggableConsoleApp.NullableToString?" },
		];
		debugProtocolHost.WithVariablesRequest(variablesReference, out var compositeVariables);
		compositeVariables.Should().HaveCount(expectedVariables.Count);
		compositeVariables.ShouldBeEquivalentToDebuggerVariables(expectedVariables);
	}
}
