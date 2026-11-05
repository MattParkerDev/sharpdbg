using System.Diagnostics;
using System.Threading;
using SharpDbg.Application;

namespace SharpDbg.Cli;

internal static class Program
{
	private static StreamWriter? _logWriter;

	public static int Main(string[] args)
	{
		var (interpreter, serverPort, logPath, requestedHelp) = Arguments.Parse(args);

		if (interpreter is null || requestedHelp)
		{
			Console.WriteLine(HelpText.Text);
			return 0;
		}

		//logPath = @"C:\Users\Matthew\Downloads\sharpdbglogs\log.txt";
		// Setup logging if specified
		if (!string.IsNullOrEmpty(logPath))
		{
			try
			{
				var logDir = Path.GetDirectoryName(logPath);
				if (!string.IsNullOrEmpty(logDir) && !Directory.Exists(logDir))
				{
					Directory.CreateDirectory(logDir);
				}
				_logWriter = new StreamWriter(logPath, append: true);
				_logWriter.AutoFlush = true;
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine($"Failed to open log file: {ex.Message}");
			}
		}

		Log($"Starting SharpDbg - Interpreter: {interpreter}");

		if (interpreter != "vscode")
		{
			Console.Error.WriteLine($"Unsupported interpreter: {interpreter}");
			Console.Error.WriteLine("Currently only --interpreter=vscode is supported");
			return 1;
		}

		try
		{
			// For now, only support stdin/stdout communication
			if (serverPort >= 0)
			{
				Console.Error.WriteLine("TCP server mode not yet implemented");
				return 1;
			}

			// Set up DAP protocol using stdin/stdout

			//Debugger.Launch();
			var inputStream = Console.OpenStandardInput();
			var outputStream = Console.OpenStandardOutput();

			// Create the debug adapter
			var adapter = new DebugAdapter(Log);

			// Initialize the protocol connection
			adapter.Initialize(inputStream, outputStream);

			Log("Protocol server starting...");

			Exception? protocolError = null;
			adapter.Connection.ProtocolError += exception =>
			{
				Interlocked.CompareExchange(ref protocolError, exception, null);

				var message = $"SharpDbg DAP protocol error: {exception}";
				Log(message);
				Console.Error.WriteLine(message);
			};

			// Shutdown happens either when a DAP 'disconnect' request has been handled
			// (while stdin is still open), or when the client closes stdin / crashes (the connection completes).
			var shutdownTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			adapter.ShutdownRequested += () => shutdownTcs.TrySetResult();
			_ = adapter.Connection.Completion.ContinueWith(_ =>
			{
				Log("Input stream closed");
				shutdownTcs.TrySetResult();
			}, TaskScheduler.Default);

			// Reads messages on a background thread and dispatches requests to the adapter
			adapter.Connection.Start();

			shutdownTcs.Task.Wait();

			// If the client closed its stream without sending a 'disconnect' request the debugger was never
			// disposed - clean it up now. No-op when a disconnect request was handled.
			adapter.DapAborted_ShutdownDebugger();

			var exitCode = protocolError is not null ? 1 : 0;
			Log($"Exiting with code {exitCode}");
			_logWriter?.Flush();
			_logWriter?.Dispose();
			_logWriter = null;

			// The protocol reader thread may be blocked reading stdin - exit explicitly rather than waiting for it.
			Environment.Exit(exitCode);
			return exitCode; // unreachable - keeps the compiler happy
		}
		catch (Exception ex)
		{
			var message = $"SharpDbg fatal error: {ex}";
			Log(message);
			Console.Error.WriteLine(message);
			return 1;
		}
		finally
		{
			_logWriter?.Dispose();
		}
	}

	private static void Log(string message)
	{
		if (_logWriter is not null)
		{
			_logWriter.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}");
		}
	}
}
