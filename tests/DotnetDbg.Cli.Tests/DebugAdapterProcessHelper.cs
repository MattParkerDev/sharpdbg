using System.Diagnostics;

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
}
