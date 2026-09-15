using SharpDbg.Application.Terminal;

namespace SharpDbg.Cli;

public static class Arguments
{
    public static (string? interpreter, int serverPort, string? logPath, bool requestedHelp, string? terminalHostConnectionPath) Parse(string[] args)
    {
        string? interpreter = null;
        var serverPort = -1;
        string? logPath = null;
        var requestedHelp = false;
        string? terminalHostConnectionPath = null;

        foreach (var arg in args)
        {
            if (arg.StartsWith("--interpreter="))
            {
                interpreter = arg["--interpreter=".Length..];
            }
            else if (arg.StartsWith("--server="))
            {
                if (int.TryParse(arg["--server=".Length..], out var port))
                {
                    serverPort = port;
                }
            }
            else if (arg.StartsWith("--engineLogging="))
            {
                logPath = arg["--engineLogging=".Length..];
            }
            else if (arg.StartsWith(TerminalHost.ConnectionOption))
            {
                terminalHostConnectionPath = arg[TerminalHost.ConnectionOption.Length..];
            }
            else if (arg.Equals("--help", StringComparison.OrdinalIgnoreCase) || arg.Equals("-h", StringComparison.OrdinalIgnoreCase))
            {
                requestedHelp = true;
            }
        }

        return (interpreter, serverPort, logPath, requestedHelp, terminalHostConnectionPath);
    }
}
