using System.Diagnostics;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace DotnetDbg.Cli.Tests;

public static class DebugAdapterProcessHelper
{
	public static Process GetDebugAdapterProcess()
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
		return process;
	}

	public static DebugProtocolHost GetDebugProtocolHost(Process process, ITestOutputHelper testOutputHelper)
	{
		var debugProtocolHost = new DebugProtocolHost(process.StandardInput.BaseStream, process.StandardOutput.BaseStream, false);
		debugProtocolHost.LogMessage += (sender, args) =>
		{
			testOutputHelper.WriteLine($"Log: {args.Message}");
		};
		debugProtocolHost.VerifySynchronousOperationAllowed();
	    debugProtocolHost.Run();
		return debugProtocolHost;
	}

	public static InitializeRequest GetInitializeRequest()
	{
		return new InitializeRequest
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
	}
}
