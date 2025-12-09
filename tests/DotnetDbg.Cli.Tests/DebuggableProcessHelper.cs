using System.Diagnostics;

namespace DotnetDbg.Cli.Tests;

public static class DebuggableProcessHelper
{
	public static Process StartDebuggableProcess(bool startSuspended = false)
	{
		var useShellExecute = !startSuspended;
		var process = new Process
		{
			StartInfo = new ProcessStartInfo
			{
				FileName = @"C:\Users\Matthew\Documents\Git\dotnetdbg\artifacts\bin\DebuggableConsoleApp\debug\DebuggableConsoleApp.exe",
				RedirectStandardInput = false,
				RedirectStandardOutput = false,
				UseShellExecute = useShellExecute,
				CreateNoWindow = false
			}
		};
		if (startSuspended) process.StartInfo.EnvironmentVariables["DOTNET_DefaultDiagnosticPortSuspend"] = "1";

		process.Start();
		return process;
	}
}
