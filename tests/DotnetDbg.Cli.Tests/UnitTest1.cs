using System.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using Newtonsoft.Json.Linq;

namespace DotnetDbg.Cli.Tests;

public class UnitTest1(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task DotnetDbgCli_InitializeRequest_Returns()
    {
	    var process = DebugAdapterProcessHelper.GetDebugAdapterProcess();
	    var debugProtocolHost = DebugAdapterProcessHelper.GetDebugProtocolHost(process, testOutputHelper);
	    var initializeRequest = DebugAdapterProcessHelper.GetInitializeRequest();

	    InitializeResponse? response = null;
	    Task.RunWithTimeout(() => response = debugProtocolHost.SendRequestSync(initializeRequest), () => process.Kill());

	    process.Kill();
	    var settings = new VerifySettings();
	    //settings.AutoVerify();

	    await Verify(response, settings);
    }

    [Fact]
    public async Task DotnetDbgCli_AttachRequest_Returns()
    {
	    var process = DebugAdapterProcessHelper.GetDebugAdapterProcess();
	    var debuggableProcess = DebuggableProcessHelper.StartDebuggableProcess();
	    try
	    {
		    var debugProtocolHost = DebugAdapterProcessHelper.GetDebugProtocolHost(process, testOutputHelper);
		    var initializeRequest = DebugAdapterProcessHelper.GetInitializeRequest();
		    debugProtocolHost.SendRequestSync(initializeRequest);
		    var attachRequest = DebugAdapterProcessHelper.GetAttachRequest(debuggableProcess.Id);
		    debugProtocolHost.SendRequestSync(attachRequest);
	    }
	    finally
	    {
		    process.Kill();
		    debuggableProcess.Kill();
	    }
    }

    [Fact]
    public async Task DotnetDbgCli_SetBreakpointsRequest_Returns()
    {
	    var process = DebugAdapterProcessHelper.GetDebugAdapterProcess();
	    var debuggableProcess = DebuggableProcessHelper.StartDebuggableProcess(false);
	    try
	    {
			var initializedEventTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		    var debugProtocolHost = DebugAdapterProcessHelper.GetDebugProtocolHost(process, testOutputHelper, initializedEventTcs);
		    var initializeRequest = DebugAdapterProcessHelper.GetInitializeRequest();
		    debugProtocolHost.SendRequestSync(initializeRequest);
		    var attachRequest = DebugAdapterProcessHelper.GetAttachRequest(debuggableProcess.Id);
		    debugProtocolHost.SendRequestSync(attachRequest);

		    await initializedEventTcs.Task;

		    var debugFilePath = @"C:\Users\Matthew\Documents\Git\dotnetdbg\tests\DebuggableConsoleApp\MyClass.cs";
		    var debugFileBreakpointLine = 9;

		    var setBreakpointsRequest = new SetBreakpointsRequest
		    {
			    Source = new Source { Path = debugFilePath },
			    Breakpoints = [new SourceBreakpoint { Line = debugFileBreakpointLine }]
		    };
		    var breakpointsResponse = debugProtocolHost.SendRequestSync(setBreakpointsRequest);
		    //await Verify(breakpointsResponse);
		    var configurationDoneRequest = new ConfigurationDoneRequest();
		    debugProtocolHost.SendRequestSync(configurationDoneRequest);
		    new DiagnosticsClient(debuggableProcess.Id).ResumeRuntime();
		    await Task.Delay(5000, TestContext.Current.CancellationToken);
	    }
	    finally
	    {
		    process.Kill();
		    debuggableProcess.Kill();
	    }
    }
}
