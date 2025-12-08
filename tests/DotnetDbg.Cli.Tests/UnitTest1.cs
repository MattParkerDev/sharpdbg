using System.Diagnostics;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace DotnetDbg.Cli.Tests;

public class UnitTest1(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void DotnetDbgCli_InitializeRequest_Returns()
    {
	    var process = DebugAdapterProcessHelper.GetDebugAdapterProcess();
	    var debugProtocolHost = DebugAdapterProcessHelper.GetDebugProtocolHost(process, testOutputHelper);
	    var initializeRequest = DebugAdapterProcessHelper.GetInitializeRequest();
	    
	    InitializeResponse? response = null;
	    var sendTask = Task.Run(() =>
	    {
		    return debugProtocolHost.SendRequestSync(initializeRequest);
	    });

		// wait up to 5 seconds
	    if (!sendTask.Wait(TimeSpan.FromSeconds(5)))
	    {
		    process.Kill();
		    throw new TimeoutException("InitializeRequest did not return within 5 seconds.");
	    }

	    response = sendTask.Result;

	    process.Kill();
    }
}
