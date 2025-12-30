using System.Diagnostics;

namespace SharpDbg.Cli.Tests.Helpers;

public static class DebuggableProcessHelper
{
	public static Process StartDebuggableProcess(bool startSuspended = false)
	{
		var useShellExecute = !startSuspended;
		const string filePath = @"C:\Users\Matthew\Documents\Git\sharpdbg\artifacts\bin\DebuggableConsoleApp\debug\DebuggableConsoleApp.exe";
		if (File.Exists(filePath) is false) throw new FileNotFoundException("DebuggableConsoleApp executable not found", filePath);
		var process = new Process
		{
			StartInfo = new ProcessStartInfo
			{
				FileName = filePath,
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
