using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using SharpDbg.Infrastructure.Debugger.Models;

namespace SharpDbg.Application.Terminal;

public sealed class TerminalLauncher : IDisposable
{
	private const int ConnectionTimeoutMs = 60000;
	private const int LaunchTimeoutMs = 30000;

	private readonly NamedPipeServerStream _pipeServer;

	/// <summary>
	/// The pipe name on Windows, or the full path of the underlying unix domain socket elsewhere - a rooted name is
	/// used as-is by both <see cref="NamedPipeServerStream"/> and <see cref="NamedPipeClientStream"/>
	/// </summary>
	public string ConnectionPath { get; }

	public TerminalLauncher()
	{
		var pipeName = $"sharpdbg-{Guid.NewGuid():N}";
		ConnectionPath = OperatingSystem.IsWindows() ? pipeName : Path.Combine(Path.GetTempPath(), $"CoreFxPipe_{pipeName}");
		_pipeServer = new NamedPipeServerStream(ConnectionPath, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
	}

	public RunInTerminalRequest CreateRunInTerminalRequest(LaunchRequestConsoleType consoleType, string title)
	{
		var executablePath = Environment.ProcessPath ?? throw new InvalidOperationException("The path of the debugger executable could not be determined");
		var arguments = new List<string> { executablePath };
		if (Path.GetFileNameWithoutExtension(executablePath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
		{
			// The debugger was started as 'dotnet SharpDbg.Cli.dll ...' rather than via an apphost executable - the
			// terminal host must be started with the same entry assembly
			var entryAssemblyPath = Assembly.GetEntryAssembly()?.Location;
			if (string.IsNullOrEmpty(entryAssemblyPath)) throw new InvalidOperationException("The entry assembly of the debugger could not be determined");
			arguments.Add(entryAssemblyPath);
		}
		arguments.Add($"{TerminalHost.ConnectionOption}{ConnectionPath}");

		var request = new RunInTerminalRequest
		{
			Kind = consoleType switch
			{
				LaunchRequestConsoleType.IntegratedTerminal => RunInTerminalArguments.KindValue.Integrated,
				LaunchRequestConsoleType.ExternalTerminal => RunInTerminalArguments.KindValue.External,
				_ => throw new ArgumentOutOfRangeException(nameof(consoleType), $"Invalid LaunchRequestConsoleType for RunInTerminalRequest: '{consoleType}'")
			},
			Title = title,
			Arguments = arguments,
			// The debuggee's cwd is applied by the TerminalHost - the terminal's own cwd matches what vsdbg sends:
			// the debugger's directory for an external terminal and an empty cwd for an integrated one
			Cwd = consoleType is LaunchRequestConsoleType.ExternalTerminal ? Path.GetDirectoryName(executablePath) : string.Empty
		};
		// vsdbg sends an empty environment for an external terminal and none for an integrated one
		if (consoleType is LaunchRequestConsoleType.ExternalTerminal)
		{
			request.Env = new Dictionary<string, object>();
		}
		return request;
	}

	/// <summary>
	/// Waits for the terminal host to connect, sends it the launch details and returns the process ID of the debuggee it started
	/// </summary>
	public int LaunchProgram(LaunchInfo launchInfo)
	{
		WaitForHost();

		var reader = new StreamReader(_pipeServer);
		var writer = new StreamWriter(_pipeServer) { AutoFlush = true };
		writer.WriteLine(JsonSerializer.Serialize(new TerminalLaunchRequest
		{
			Program = launchInfo.Program,
			Arguments = launchInfo.Arguments,
			WorkingDirectory = launchInfo.Cwd,
			Environment = launchInfo.Env
		}));

		var readTask = Task.Run(() => reader.ReadLine());
		if (readTask.Wait(LaunchTimeoutMs) is false) throw new TimeoutException("Timed out waiting for the terminal host to launch the debuggee");

		var response = readTask.Result is null ? null : JsonSerializer.Deserialize<TerminalLaunchResponse>(readTask.Result);
		if (response?.ProcessId is null) throw new InvalidOperationException($"The terminal host failed to launch the debuggee: {response?.Error ?? "no response"}");
		return response.ProcessId.Value;
	}

	public void Dispose()
	{
		_pipeServer.Dispose();
	}

	private void WaitForHost()
	{
		// WaitForConnectionAsync completes immediately if the host connected before this was called
		try
		{
			using var timeoutSource = new CancellationTokenSource(ConnectionTimeoutMs);
			_pipeServer.WaitForConnectionAsync(timeoutSource.Token).GetAwaiter().GetResult();
		}
		catch (OperationCanceledException)
		{
			throw new TimeoutException("Timed out waiting for the terminal host to connect");
		}
	}
}
