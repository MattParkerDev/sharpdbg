using System.Diagnostics;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace DotnetDbg.Cli.Tests;

public class UnitTest1(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void DotnetDbgCli_InitializeRequest_Returns()
    {
	    var process = new Process
	    {
		    StartInfo = new ProcessStartInfo
		    {
			    //FileName = @"C:\Users\Matthew\Downloads\netcoredbg-win64\netcoredbg\netcoredbg.exe",
			    FileName = @"C:\Users\Matthew\Documents\Git\dotnetdbg\artifacts\bin\DotnetDbg.Cli\debug\DotnetDbg.Cli.exe",
			    Arguments = "--interpreter=vscode",
			    RedirectStandardInput = true,
			    RedirectStandardOutput = true,
			    UseShellExecute = false,
			    CreateNoWindow = true
		    }
	    };
	    process.Start();

	    var debugProtocolHost = new DebugProtocolHost(process.StandardInput.BaseStream, process.StandardOutput.BaseStream, false);
	    debugProtocolHost.LogMessage += (sender, args) =>
	    {
		    testOutputHelper.WriteLine($"Log: {args.Message}");
	    };
	    debugProtocolHost.VerifySynchronousOperationAllowed();
	    var initializeRequest = new InitializeRequest
	    {
		    ClientID = "vscode",
		    ClientName = "Visual Studio Code",
		    AdapterID = "coreclr",
		    Locale = "en-us",
		    LinesStartAt1 = true,
		    ColumnsStartAt1 = true,
		    PathFormat = InitializeArguments.PathFormatValue.Path,
		    SupportsVariableType = true,
		    SupportsVariablePaging = true,
		    SupportsRunInTerminalRequest = true,
		    SupportsHandshakeRequest = true
	    };
	    debugProtocolHost.Run();
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
