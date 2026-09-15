using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

namespace SharpDbg.Application.Terminal;

public static class TerminalHost
{
	public const string ConnectionOption = "--connection=";
	private const int ConnectionTimeoutMs = 10000;

	public static int Run(string connectionPath)
	{
		try
		{
			using var pipe = new NamedPipeClientStream(".", connectionPath, PipeDirection.InOut);
			pipe.Connect(ConnectionTimeoutMs);

			var reader = new StreamReader(pipe);
			var writer = new StreamWriter(pipe) { AutoFlush = true };
			var requestLine = reader.ReadLine();
			var request = requestLine is null ? null : JsonSerializer.Deserialize<TerminalLaunchRequest>(requestLine);
			if (request is null || string.IsNullOrEmpty(request.Program)) return 1;

			try
			{
				var process = StartProcess(request);
				writer.WriteLine(JsonSerializer.Serialize(new TerminalLaunchResponse { ProcessId = process.Id }));
				process.WaitForExit();
				return process.ExitCode;
			}
			catch (Exception ex)
			{
				writer.WriteLine(JsonSerializer.Serialize(new TerminalLaunchResponse { Error = ex.Message }));
				return 1;
			}
		}
		catch
		{
			// The pipe could not be reached or broke mid-handshake - the adapter is gone, so there is nowhere to report the failure
			return 1;
		}
	}

	private static Process StartProcess(TerminalLaunchRequest request)
	{
		var processStartInfo = new ProcessStartInfo
		{
			FileName = request.Program,
			WorkingDirectory = request.WorkingDirectory ?? Environment.CurrentDirectory,
			UseShellExecute = false
		};
		foreach (var argument in request.Arguments)
		{
			processStartInfo.ArgumentList.Add(argument);
		}

		// The runtime waits suspended for a diagnostics client before running any managed code, so the adapter can attach first
		processStartInfo.Environment["DOTNET_DefaultDiagnosticPortSuspend"] = "1";
		foreach (var (key, value) in request.Environment)
		{
			processStartInfo.Environment[key] = value;
		}

		return Process.Start(processStartInfo) ?? throw new InvalidOperationException("Process start failed");
	}
}
