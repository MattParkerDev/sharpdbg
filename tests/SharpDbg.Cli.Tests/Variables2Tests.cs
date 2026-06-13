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
			.WithBreakpointsRequest([137], breakpointedFilePath)
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(debugEventTcs);
		stoppedEvent.ReadStopInfo().Should().Be((breakpointedFilePath, 137, 3));
		debugProtocolHost
			.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTraceResponse)
			.WithScopesRequest(stackTraceResponse.StackFrames!.First().Id, out var scopesResponse);

		scopesResponse.Scopes.Should().HaveCount(1);
		var scope = scopesResponse.Scopes.Single();

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
			new() { VariablesReference = 5,  Name = "localArray",           	EvaluateName = "localArray",           		Value = "int[3]",									Type = "int[]" },
			new() { VariablesReference = 6,  Name = "localList",            	EvaluateName = "localList",            		Value = "Count = 2",								Type = "System.Collections.Generic.List<string>" },
			new() { VariablesReference = 7,  Name = "localDictionary",      	EvaluateName = "localDictionary",      		Value = "Count = 1",								Type = "System.Collections.Generic.Dictionary<int, string>" },
			new() { VariablesReference = 8,  Name = "localStruct",          	EvaluateName = "localStruct",          		Value = "{DebuggableConsoleApp.TestStruct}",		Type = "DebuggableConsoleApp.TestStruct" },
			new() { VariablesReference = 9,  Name = "localClass",           	EvaluateName = "localClass",           		Value = "{DebuggableConsoleApp.TestClass}",			Type = "DebuggableConsoleApp.TestClass" },
			new() { VariablesReference = 10, Name = "localRecord",          	EvaluateName = "localRecord",          		Value = "{TestRecord { Name = record, Age = 1 }}",	Type = "DebuggableConsoleApp.TestRecord" },
			new() { VariablesReference = 11, Name = "localInterface",       	EvaluateName = "localInterface",       		Value = "{DebuggableConsoleApp.TestClass}",			Type = "DebuggableConsoleApp.ITestInterface {DebuggableConsoleApp.TestClass}" },
			new() { VariablesReference = 12, Name = "localDelegate",        	EvaluateName = "localDelegate",        		Value = "{System.Func<int, int>}",					Type = "System.Func<int, int>" },
			new() { VariablesReference = 13, Name = "localTuple",           	EvaluateName = "localTuple",           		Value = "{(1, stringInTuple)}",                 	Type = "System.Tuple<int, string>" },
			new() { VariablesReference = 14, Name = "localValueTuple",      	EvaluateName = "localValueTuple",      		Value = "(2, \"stringInValueTuple\")",				Type = "(int A, string B)" },
			new() { VariablesReference = 15, Name = "localGeneric",         	EvaluateName = "localGeneric",         		Value = "{DebuggableConsoleApp.GenericBox<int>}",	Type = "DebuggableConsoleApp.GenericBox<int>" },
			new() { VariablesReference = 0,  Name = "localDynamic",         	EvaluateName = "localDynamic",         		Value = "241",                                  	Type = "object {int}" },
			new() { VariablesReference = 16, Name = "localAnonymous",       	EvaluateName = "localAnonymous",       		Value = "{ Id = 1, Name = \"Anonymous\" }",         Type = "<Anonymous Type>" },
			new() { VariablesReference = 17, Name = "localDateTime",        	EvaluateName = "localDateTime",        		Value = "13/06/2026 5:42:39 AM",					Type = "System.DateTime" },
			new() { VariablesReference = 18, Name = "localGuid",            	EvaluateName = "localGuid",            		Value = "27de5b68-af24-4e59-a785-dde52e2ea7af",		Type = "System.Guid" },
		];

		debugProtocolHost.WithVariablesRequest(scope.VariablesReference, out var variables);

		variables.Should().HaveCount(37);
		variables.Should().BeEquivalentTo(expectedVariables, options => options.Excluding(s => s.MemoryReference).Excluding(s => s.PresentationHint));
	}
}

file static class TestExtensions
{

}
