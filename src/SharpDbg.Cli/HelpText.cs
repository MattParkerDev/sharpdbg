namespace SharpDbg.Cli;

public class HelpText
{
	public const string Text = """
	                           SharpDbg - .NET Core Debugger

	                           Options:
	                           --interpreter=<name>                 Specifies the debugger interpreter to use,
	                                                                currently only "vscode" is supported. [Required]
	                           --engineLogging=<path to log file>   Enable debug engine logging to a file.
	                           """;
}
