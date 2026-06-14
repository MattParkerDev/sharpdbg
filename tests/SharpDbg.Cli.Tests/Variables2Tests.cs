using AwesomeAssertions;
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

		var (debugProtocolHost, initializedEventTcs, debugEventTcs, adapter, p2) = TestHelper.GetRunningDebugProtocolHostInProc(testOutputHelper, startSuspended);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(p2.Id)
			.WaitForInitializedEvent(initializedEventTcs);
		var breakpointedFilePath = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "VariablesClass.cs");
		debugProtocolHost
			.WithBreakpointsRequest([138], breakpointedFilePath)
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(debugEventTcs);
		stoppedEvent.ReadStopInfo().Should().Be((breakpointedFilePath, 138, 3));
		debugProtocolHost
			.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTraceResponse)
			.WithScopesRequest(stackTraceResponse.StackFrames!.First().Id, out var scopesResponse);

		scopesResponse.Scopes.Should().HaveCount(1);
		var scope = scopesResponse.Scopes.Single();

		var expectedDateTimeString = new DateTime(2026, 6, 13, 5, 42, 39).ToString();
		List<Variable> expectedVariables =
		[
			new() { VariablesReference = 3,  Name = "this",						EvaluateName = "this",                 		Value = "{DebuggableConsoleApp.VariablesClass}",	Type = "DebuggableConsoleApp.VariablesClass" },
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
			new() { VariablesReference = 0,  Name = "localString",          	EvaluateName = "localString",          		Value = "hello",									Type = "string" },
			new() { VariablesReference = 0,  Name = "localNullableString",  	EvaluateName = "localNullableString",  		Value = "null",										Type = "string" },
			new() { VariablesReference = 4,  Name = "localObject",          	EvaluateName = "localObject",          		Value = "{object}",									Type = "object" },
			new() { VariablesReference = 0,  Name = "localNullableObject",  	EvaluateName = "localNullableObject",  		Value = "null",										Type = "object" },
			new() { VariablesReference = 0,  Name = "localBoxedInt",  			EvaluateName = "localBoxedInt",  			Value = "42",										Type = "int" },
			new() { VariablesReference = 5,  Name = "localArray",           	EvaluateName = "localArray",           		Value = "int[3]",									Type = "int[]" },
			new() { VariablesReference = 6,  Name = "localList",            	EvaluateName = "localList",            		Value = "Count = 2",								Type = "System.Collections.Generic.List<string>" },
			new() { VariablesReference = 7,  Name = "localDictionary",      	EvaluateName = "localDictionary",      		Value = "Count = 1",								Type = "System.Collections.Generic.Dictionary<int, string>" },
			new() { VariablesReference = 8,  Name = "localStruct",          	EvaluateName = "localStruct",          		Value = "{DebuggableConsoleApp.TestStruct}",		Type = "DebuggableConsoleApp.TestStruct" },
			new() { VariablesReference = 9,  Name = "localClass",           	EvaluateName = "localClass",           		Value = "{DebuggableConsoleApp.TestClass}",			Type = "DebuggableConsoleApp.TestClass" },
			new() { VariablesReference = 10, Name = "localRecord",          	EvaluateName = "localRecord",          		Value = "TestRecord { Name = record, Age = 1 }",	Type = "DebuggableConsoleApp.TestRecord" },
			new() { VariablesReference = 11, Name = "localInterface",       	EvaluateName = "localInterface",       		Value = "{DebuggableConsoleApp.TestClass}",			Type = "DebuggableConsoleApp.TestClass" },
			new() { VariablesReference = 12, Name = "localDelegate",        	EvaluateName = "localDelegate",        		Value = "{System.Func<int, int>}",					Type = "System.Func<int, int>" },
			new() { VariablesReference = 13, Name = "localTuple",           	EvaluateName = "localTuple",           		Value = "(1, stringInTuple)",                 		Type = "System.Tuple<int, string>" },
			new() { VariablesReference = 14, Name = "localValueTuple",      	EvaluateName = "localValueTuple",      		Value = "(2, stringInValueTuple)",					Type = "System.ValueTuple<int, string>" },
			new() { VariablesReference = 15, Name = "localGeneric",         	EvaluateName = "localGeneric",         		Value = "{DebuggableConsoleApp.GenericBox<int>}",	Type = "DebuggableConsoleApp.GenericBox<int>" },
			new() { VariablesReference = 0,  Name = "localDynamic",         	EvaluateName = "localDynamic",         		Value = "241",                                  	Type = "int" },
			new() { VariablesReference = 16, Name = "localAnonymous",       	EvaluateName = "localAnonymous",       		Value = "{ Id = 1, Name = Anonymous }",				Type = "<>f__AnonymousType0<int, string>" },
			new() { VariablesReference = 17, Name = "localDateTime",        	EvaluateName = "localDateTime",        		Value = expectedDateTimeString,						Type = "System.DateTime" },
			new() { VariablesReference = 18, Name = "localGuid",            	EvaluateName = "localGuid",            		Value = "27de5b68-af24-4e59-a785-dde52e2ea7af",		Type = "System.Guid" },
		];

		debugProtocolHost.WithVariablesRequest(scope.VariablesReference, out var variables);

		variables.Should().HaveCount(38);
		variables.Should().BeEquivalentTo(expectedVariables, options => options.Excluding(s => s.MemoryReference).Excluding(s => s.PresentationHint));
		debugProtocolHost.AssertInstanceThisInstanceVariables(variables.Single(s => s.Name == "this").VariablesReference);
	}
}

file static class TestExtensions
{
	public static void AssertInstanceThisInstanceVariables(this DebugProtocolHost debugProtocolHost, int variablesReference)
	{
		var expectedDateTimeField = new DateTime(2026, 6, 15, 10, 5, 8).ToString();
		var expectedDateOnlyField = new DateOnly(2026, 6, 15).ToString();
		var expectedTimeOnlyField = new TimeOnly(10, 2, 16).ToString();
		var expectedTimeSpanField = TimeSpan.FromMinutes(5).ToString();
		var expectedGuidField = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
		var expectedNullableGuidField= "f0e1d2c3-b4a5-9687-7869-5a4b3c2d1e0f";

		List<Variable> expectedVariables =
		[
			new() { VariablesReference =  0,  Name = "BoolField",              	EvaluateName = "BoolField",              	Value = "true",                                         Type = "bool" },
			new() { VariablesReference =  0,  Name = "ByteField",              	EvaluateName = "ByteField",              	Value = "1",                                            Type = "byte" },
			new() { VariablesReference =  0,  Name = "SByteField",             	EvaluateName = "SByteField",             	Value = "-1",                                           Type = "sbyte" },
			new() { VariablesReference =  0,  Name = "ShortField",             	EvaluateName = "ShortField",             	Value = "-2",                                           Type = "short" },
			new() { VariablesReference =  0,  Name = "UShortField",            	EvaluateName = "UShortField",            	Value = "2",                                            Type = "ushort" },
			new() { VariablesReference =  0,  Name = "IntField",               	EvaluateName = "IntField",               	Value = "123",                                          Type = "int" },
			new() { VariablesReference =  0,  Name = "UIntField",              	EvaluateName = "UIntField",              	Value = "123",                                          Type = "uint" },
			new() { VariablesReference =  0,  Name = "LongField",              	EvaluateName = "LongField",              	Value = "123456789",                                    Type = "long" },
			new() { VariablesReference =  0,  Name = "ULongField",             	EvaluateName = "ULongField",             	Value = "123456789",                                    Type = "ulong" },
			new() { VariablesReference =  0,  Name = "CharField",              	EvaluateName = "CharField",              	Value = "65 'A'",                                       Type = "char" },
			new() { VariablesReference =  0,  Name = "FloatField",             	EvaluateName = "FloatField",             	Value = "1.23",                                         Type = "float" },
			new() { VariablesReference =  0,  Name = "DoubleField",            	EvaluateName = "DoubleField",            	Value = "2.34",                                         Type = "double" },
			new() { VariablesReference =  0,  Name = "DecimalField",           	EvaluateName = "DecimalField",           	Value = "3.45",                                         Type = "decimal" },
			new() { VariablesReference =  0,  Name = "NullableIntField",       	EvaluateName = "NullableIntField",       	Value = "42",                                           Type = "int?" },
			new() { VariablesReference =  0,  Name = "NullableIntNullField",   	EvaluateName = "NullableIntNullField",   	Value = "null",                                         Type = "int?" },
			new() { VariablesReference =  0,  Name = "NullableBoolField",      	EvaluateName = "NullableBoolField",      	Value = "true",                                         Type = "bool?" },
			new() { VariablesReference =  0,  Name = "NullableBoolNullField",  	EvaluateName = "NullableBoolNullField",  	Value = "null",                                         Type = "bool?" },
			new() { VariablesReference = 19,  Name = "NullableGuidField",      	EvaluateName = "NullableGuidField",      	Value = expectedNullableGuidField,                      Type = "System.Guid?" },
			new() { VariablesReference = 20,  Name = "NullableEnumField",      	EvaluateName = "NullableEnumField",      	Value = "Friday",                                       Type = "System.DayOfWeek?" },
			new() { VariablesReference =  0,  Name = "NullableEnumNullField",  	EvaluateName = "NullableEnumNullField",  	Value = "null",                                         Type = "System.DayOfWeek?" },
			new() { VariablesReference =  0,  Name = "StringField",            	EvaluateName = "StringField",            	Value = "Hello",                                        Type = "string" },
			new() { VariablesReference =  0,  Name = "NullableStringField",    	EvaluateName = "NullableStringField",    	Value = "null",                                         Type = "string" },
			new() { VariablesReference = 21,  Name = "ObjectField",            	EvaluateName = "ObjectField",            	Value = "{object}",                                     Type = "object" },
			new() { VariablesReference =  0,  Name = "NullableObjectField",    	EvaluateName = "NullableObjectField",    	Value = "null",                                         Type = "object" },
			new() { VariablesReference = 22,  Name = "IntArrayField",          	EvaluateName = "IntArrayField",          	Value = "int[3]",                                       Type = "int[]" },
			new() { VariablesReference = 23,  Name = "NullableStringArrayField",EvaluateName = "NullableStringArrayField",	Value = "string[3]",									Type = "string[]" },
			new() { VariablesReference = 24,  Name = "MultiDimArrayField",     	EvaluateName = "MultiDimArrayField",     	Value = "int[2, 2]",                                    Type = "int[,]" },
			new() { VariablesReference = 25,  Name = "JaggedArrayField",       	EvaluateName = "JaggedArrayField",       	Value = "int[][2]",                                     Type = "int[][]" },
			new() { VariablesReference = 26,  Name = "ListField",              	EvaluateName = "ListField",              	Value = "Count = 3",                                    Type = "System.Collections.Generic.List<int>" },
			new() { VariablesReference = 27,  Name = "DictionaryField",        	EvaluateName = "DictionaryField",        	Value = "Count = 2",                                    Type = "System.Collections.Generic.Dictionary<string, int>" },
			new() { VariablesReference = 28,  Name = "DateTimeField",          	EvaluateName = "DateTimeField",          	Value = expectedDateTimeField,                          Type = "System.DateTime" },
			new() { VariablesReference = 29,  Name = "DateOnlyField",          	EvaluateName = "DateOnlyField",          	Value = expectedDateOnlyField,                          Type = "System.DateOnly" },
			new() { VariablesReference = 30,  Name = "TimeOnlyField",          	EvaluateName = "TimeOnlyField",          	Value = expectedTimeOnlyField,                          Type = "System.TimeOnly" },
			new() { VariablesReference = 31,  Name = "TimeSpanField",          	EvaluateName = "TimeSpanField",          	Value = expectedTimeSpanField,                          Type = "System.TimeSpan" },
			new() { VariablesReference = 32,  Name = "GuidField",              	EvaluateName = "GuidField",              	Value = expectedGuidField,                              Type = "System.Guid" },
			new() { VariablesReference = 33,  Name = "EnumField",              	EvaluateName = "EnumField",              	Value = "Monday",                                       Type = "System.DayOfWeek" },
			new() { VariablesReference = 34,  Name = "StructField",            	EvaluateName = "StructField",            	Value = "{DebuggableConsoleApp.TestStruct}",            Type = "DebuggableConsoleApp.TestStruct" },
			new() { VariablesReference = 35,  Name = "ClassField",             	EvaluateName = "ClassField",             	Value = "{DebuggableConsoleApp.TestClass}",             Type = "DebuggableConsoleApp.TestClass" },
			new() { VariablesReference = 36,  Name = "RecordField",            	EvaluateName = "RecordField",            	Value = "TestRecord { Name = Alice, Age = 42 }",        Type = "DebuggableConsoleApp.TestRecord" },
			new() { VariablesReference = 37,  Name = "RecordStructField",      	EvaluateName = "RecordStructField",      	Value = "TestRecordStruct { Value = 7 }",               Type = "DebuggableConsoleApp.TestRecordStruct" },
			new() { VariablesReference = 38,  Name = "InterfaceField",         	EvaluateName = "InterfaceField",         	Value = "{DebuggableConsoleApp.TestClass}",             Type = "DebuggableConsoleApp.TestClass" },
			new() { VariablesReference = 39,  Name = "DelegateField",          	EvaluateName = "DelegateField",          	Value = "{System.Func<int, int>}",                      Type = "System.Func<int, int>" },
			new() { VariablesReference = 40,  Name = "TupleField",             	EvaluateName = "TupleField",             	Value = "(1, tuple)",                                   Type = "System.Tuple<int, string>" },
			new() { VariablesReference = 41,  Name = "ValueTupleField",        	EvaluateName = "ValueTupleField",        	Value = "(123, value tuple)",                           Type = "System.ValueTuple<int, string>" },
			new() { VariablesReference = 42,  Name = "GenericField",           	EvaluateName = "GenericField",           	Value = "{DebuggableConsoleApp.GenericBox<string>}",    Type = "DebuggableConsoleApp.GenericBox<string>" },
			new() { VariablesReference =  0,  Name = "DynamicField",           	EvaluateName = "DynamicField",           	Value = "dynamic value",                                Type = "string" },
			new() { VariablesReference =  0,  Name = "ReadonlyField",          	EvaluateName = "ReadonlyField",          	Value = "readonly",                                     Type = "string" },
			new() { VariablesReference =  0,  Name = "IntProperty",            	EvaluateName = "IntProperty",            	Value = "100",                                          Type = "int" },
			new() { VariablesReference =  0,  Name = "NullableStringProperty", 	EvaluateName = "NullableStringProperty", 	Value = "null",                                         Type = "string" },
			new() { VariablesReference = 43,  Name = "ClassProperty",          	EvaluateName = "ClassProperty",          	Value = "{DebuggableConsoleApp.TestClass}",             Type = "DebuggableConsoleApp.TestClass" },
			new() { VariablesReference = 44,  Name = "RecordProperty",         	EvaluateName = "RecordProperty",         	Value = "TestRecord { Name = InitProperty, Age = 5 }",	Type = "DebuggableConsoleApp.TestRecord" },
			new() { VariablesReference =  0,  Name = "ComputedProperty",       	EvaluateName = "ComputedProperty",       	Value = "246",                                          Type = "int" },
			new() { VariablesReference = 45,  Name = "Static members",			EvaluateName = "Static members",			Value = "",												Type = ""},
		];
		debugProtocolHost.WithVariablesRequest(variablesReference, out var thisInstanceVariables);
		thisInstanceVariables.Should().HaveCount(53);
		thisInstanceVariables.Should().BeEquivalentTo(expectedVariables, options => options.Excluding(s => s.MemoryReference).Excluding(s => s.PresentationHint));
	}
}
