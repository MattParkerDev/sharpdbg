using AwesomeAssertions;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using Newtonsoft.Json.Linq;
using SharpDbg.Application.Protocol;
using SharpDbg.Cli.Tests.Helpers;

namespace SharpDbg.Cli.Tests;

public class StackTraceTests(ITestOutputHelper testOutputHelper)
{
	[Fact]
	public async Task StackTraceRequest_ReturnsFrames()
	{
		var startSuspended = true;
		var (debugProtocolHost, initializedEventTcs, debugEventTcs, adapter, p2) = TestHelper.GetRunningDebugProtocolHostInProc(testOutputHelper, startSuspended);
		using var _ = adapter;
		using var __ = new ProcessKiller(p2);
		using var ___ = debugProtocolHost;


		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(p2.Id, justMyCode: true)
			.WaitForInitializedEvent(initializedEventTcs);
		debugProtocolHost.SendRequestSync(new SetExceptionBreakpointsRequest { Filters = [], FilterOptions = [new("all"), new("user-unhandled")] });
		var breakpointedFilePath = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "ClassWithBclCall.cs");
		debugProtocolHost
			.WithBreakpointsRequest([12], breakpointedFilePath)
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(p2.Id, startSuspended);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(debugEventTcs);
		var stopInfo = stoppedEvent.ReadStopInfo();
		stopInfo.filePath.Should().EndWith("ClassWithBclCall.cs");
		stopInfo.line.Should().Be(12);

		// TODO: Handle formatting generic methods (and fully qualifying them), e.g. System.Linq.dll!System.Linq.Enumerable.RangeSelectIterator<int, int>.Fill() instead of System.Linq.dll!RangeSelectIterator`2.Fill()
		List<StackFrame> expectedStackFrames =
		[
			new() { Id = 1, Column = 3, EndColumn = 16,	Line = 12, EndLine = 12, Name = "DebuggableConsoleApp.dll!DebuggableConsoleApp.ClassWithBclCall.Selector(int x)",      Source = new Source { Name = "ClassWithBclCall.cs", SourceReference = 0, Path = breakpointedFilePath } },
			new() { Id = 2, Column = 0, EndColumn =  0, Line =  0, EndLine =  0, Name = "System.Linq.dll!System.Linq.Enumerable.RangeSelectIterator<int, int>.Fill(System.Span<int> results, int start, System.Func<int, int> func)",    Source = null },
			new() { Id = 3, Column = 0, EndColumn =  0, Line =  0, EndLine =  0, Name = "System.Linq.dll!System.Linq.Enumerable.RangeSelectIterator<int, int>.ToArray()", Source = null },
			new() { Id = 4, Column = 3, EndColumn = 65, Line =  7, EndLine =  7, Name = "DebuggableConsoleApp.dll!DebuggableConsoleApp.ClassWithBclCall.Test(int myParam)",          Source = new Source { Name = "ClassWithBclCall.cs", SourceReference = 0, Path = breakpointedFilePath } },
			new() { Id = 5, Column = 4, EndColumn = 29, Line = 31, EndLine = 31, Name = "DebuggableConsoleApp.dll!DebuggableConsoleApp.Program.Main(string[] args)",                   Source = new Source { Name = "Program.cs",          SourceReference = 0, Path = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "Program.cs") } },
			// TODO: Return internal frames (thread.ActiveInternalFrames)
			//new() { Id = 1005, Column = 0, EndColumn = null,  Line =  0, EndLine = null, Name = "[Native to Managed Transition]", Source = null, PresentationHint = StackFrame.PresentationHintValue.Subtle }
		];

		debugProtocolHost.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTraceResponse, null);
		stackTraceResponse.StackFrames.Count.Should().Be(expectedStackFrames.Count);
		stackTraceResponse.StackFrames.Should().BeEquivalentTo(expectedStackFrames, options => options.WithStrictOrdering().Excluding(s => s.Source.Checksums).Excluding(s => s.Source.VsSourceLinkInfo).Excluding(s => s.InstructionPointerReference).Excluding(s => s.AdditionalProperties));
		stackTraceResponse.StackFrames.Select(f => f.IsResolved).Should().Equal(true, false, false, true, true);
		// Since JMC is enabled, we never decompile, as well as to resolve a frame, we need to send a ResolveStackFrameRequest, so all frames should have null decompiledSourceInfo
		stackTraceResponse.StackFrames.Should().AllSatisfy(frame => frame.DecompiledSourceInfo.Should().BeNull());

		var breakpointedFilePath2 = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "MyClassContainingAnotherClass.cs");
		var stoppedEvent2 = await debugProtocolHost
			.WithBreakpointsRequest([], breakpointedFilePath)
			.WithBreakpointsRequest([19], breakpointedFilePath2)
			.WithContinueRequest()
			.WaitForStoppedEvent(debugEventTcs);

		var stopInfo2 = stoppedEvent2.ReadStopInfo();
		stopInfo2.filePath.Should().EndWith("MyClassContainingAnotherClass.cs");
		stopInfo2.line.Should().Be(19);

		List<StackFrame> expectedStackFrames2 =
		[
			new() { Id = 1, Column = 4, EndColumn =   5, Line = 19, EndLine = 19, Name = "DebuggableConsoleApp.dll!DebuggableConsoleApp.MyGenericClassContainingAnotherGenericClass<int, string>.MyNestedGenericClass<double, bool>.Test()", Source = new Source { Name = "MyClassContainingAnotherClass.cs", SourceReference = 0, Path = breakpointedFilePath2 } },
			new() { Id = 2, Column = 4, EndColumn = 103, Line = 35, EndLine = 35, Name = "DebuggableConsoleApp.dll!DebuggableConsoleApp.Program.Main(string[] args)", Source = new Source { Name = "Program.cs", SourceReference = 0, Path = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "Program.cs") } },
		];

		debugProtocolHost.WithStackTraceRequest(stoppedEvent2.ThreadId!.Value, out var stackTraceResponse2, null);
		stackTraceResponse2.StackFrames.Count.Should().Be(expectedStackFrames2.Count);
		stackTraceResponse2.StackFrames.Should().BeEquivalentTo(expectedStackFrames2, options => options.WithStrictOrdering().Excluding(s => s.Source.Checksums).Excluding(s => s.Source.VsSourceLinkInfo).Excluding(s => s.InstructionPointerReference).Excluding(s => s.AdditionalProperties));

		var breakpointedFilePath3 = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "Namespace1", "AnotherClass.cs");
		var stoppedEvent3 = await debugProtocolHost
			.WithBreakpointsRequest([], breakpointedFilePath2)
			.WithBreakpointsRequest([20], breakpointedFilePath3)
			.WithContinueRequest()
			.WaitForStoppedEvent(debugEventTcs);

		var stopInfo3 = stoppedEvent3.ReadStopInfo();
		stopInfo3.filePath.Should().EndWith("AnotherClass.cs");
		stopInfo3.line.Should().Be(20);

		debugProtocolHost.WithStackTraceRequest(stoppedEvent3.ThreadId!.Value, out var stackTraceResponse3, null);
		var userFrameNames = stackTraceResponse3.StackFrames
			.Select(frame => frame.Name)
			.Where(name => name.StartsWith("DebuggableConsoleApp.dll!"))
			.ToList();
		userFrameNames.Should().Contain("DebuggableConsoleApp.dll!DebuggableConsoleApp.Namespace1.AnotherClass.AnotherMethodAsync()");
		userFrameNames.Should().Contain("DebuggableConsoleApp.dll!DebuggableConsoleApp.MyAsyncClass.MyMethodAsync(int myParam)");
		userFrameNames.Should().NotContain(name => name.Contains("MoveNext"));

	}

	[Fact]
	public async Task ResolveStackFrameRequest_DecompilesUnresolvedFrame()
	{
		var startSuspended = true;
		var (debugProtocolHost, initializedEventTcs, debugEventTcs, adapter, process) = TestHelper.GetRunningDebugProtocolHostInProc(testOutputHelper, startSuspended);
		using var _ = adapter;
		using var __ = new ProcessKiller(process);
		using var ___ = debugProtocolHost;

		await debugProtocolHost
			.WithInitializeRequest()
			.WithAttachRequest(process.Id, justMyCode: false)
			.WaitForInitializedEvent(initializedEventTcs);
		var breakpointedFilePath = Path.JoinFromGitRoot("tests", "DebuggableConsoleApp", "ClassWithBclCall.cs");
		debugProtocolHost
			.WithBreakpointsRequest([12], breakpointedFilePath)
			.WithConfigurationDoneRequest()
			.WithOptionalResumeRuntime(process.Id, startSuspended);

		var stoppedEvent = await debugProtocolHost.WaitForStoppedEvent(debugEventTcs);
		debugProtocolHost.WithStackTraceRequest(stoppedEvent.ThreadId!.Value, out var stackTraceResponse, null);
		var unresolvedFrame = stackTraceResponse.StackFrames.First(frame => frame.Name.StartsWith("System.Linq.dll!"));
		unresolvedFrame.IsResolved.Should().BeFalse();
		unresolvedFrame.DecompiledSourceInfo.Should().BeNull();

		var response = debugProtocolHost.SendRequestSync(new ResolveStackFrameRequest(unresolvedFrame.Id));

		response.StackFrame.Id.Should().Be(unresolvedFrame.Id);
		response.StackFrame.IsResolved.Should().BeTrue();
		response.StackFrame.Source.Path.Should().EndWith(".cs");
		response.StackFrame.Line.Should().BeGreaterThan(0);
		var decompiledSourceInfo = response.StackFrame.DecompiledSourceInfo;
		decompiledSourceInfo.Should().NotBeNull();
		decompiledSourceInfo.TypeFullName.Should().Be("System.Linq.Enumerable+RangeSelectIterator`2");
		decompiledSourceInfo.Assembly.AssemblyPath.Should().EndWith("System.Linq.dll");
		decompiledSourceInfo.CallingUserCodeAssemblyPath.Should().EndWith("DebuggableConsoleApp.dll");
	}
}
