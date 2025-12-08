using System.Diagnostics;
using System.Threading;
using DotnetDbg.Application;

namespace DotnetDbg.Cli;

class Program
{
    private static StreamWriter? _logWriter;

    static async Task<int> Main(string[] args)
    {
	    var interpreter = "vscode";
        var serverPort = -1;
        string? logPath = null;

        // Parse command line arguments
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--interpreter="))
            {
                interpreter = args[i].Substring("--interpreter=".Length);
            }
            else if (args[i].StartsWith("--server="))
            {
                if (int.TryParse(args[i].Substring("--server=".Length), out var port))
                {
                    serverPort = port;
                }
            }
            else if (args[i].StartsWith("--engineLogging="))
            {
                logPath = args[i].Substring("--engineLogging=".Length);
            }
        }

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

        Log($"Starting DotNetDbg - Interpreter: {interpreter}");

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
            // Note: Must run on MTA thread for ClrDebug
            Exception? threadException = null;
            var thread = new Thread(() =>
            {
                try
                {
	                //Debugger.Launch();
                    var inputStream = Console.OpenStandardInput();
                    var outputStream = Console.OpenStandardOutput();

                    // Create the debug adapter
                    var adapter = new DebugAdapter(Log);

                    // Initialize the protocol client and start it
                    adapter.Initialize(inputStream, outputStream);

                    Log("Protocol server starting...");
                    // Run() starts the protocol client's message loop in a background thread
                    adapter.Protocol.Run();
                    // WaitForReader() blocks until the input stream is closed (client disconnects)
                    adapter.Protocol.WaitForReader();
                    Log("Protocol server stopped");
                }
                catch (Exception ex)
                {
                    Log($"Fatal error: {ex.Message}");
                    Log($"Stack trace: {ex.StackTrace}");
                    threadException = ex;
                }
            });

            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            thread.Join();

            if (threadException != null)
            {
                return 1;
            }

            return 0;
        }
        catch (Exception ex)
        {
            Log($"Fatal error: {ex.Message}");
            Log($"Stack trace: {ex.StackTrace}");
            return 1;
        }
        finally
        {
            _logWriter?.Dispose();
        }
    }

    private static void Log(string message)
    {
        if (_logWriter != null)
        {
            _logWriter.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}");
        }
    }
}
