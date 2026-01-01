using System.Diagnostics;

namespace SharpDbg.Cli.Tests.Helpers;

public static class DebuggableProcessHelper
{
	public static Process StartDebuggableProcess(bool startSuspended = false)
	{
		var useShellExecute = !startSuspended;
		var filePath = Path.JoinFromGitRoot("artifacts", "bin", "DebuggableConsoleApp", "debug", OperatingSystem.IsWindows() ? "DebuggableConsoleApp.exe" : "DebuggableConsoleApp");
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
