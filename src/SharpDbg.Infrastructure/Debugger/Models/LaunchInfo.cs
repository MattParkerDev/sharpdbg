namespace SharpDbg.Infrastructure.Debugger.Models;

public class LaunchInfo
{
	public required LaunchRequestConsoleType LaunchRequestConsoleType { get; set; }
	public required string? Cwd { get; set; }
	public required string Program { get; set; }
	public required bool StopAtEntry { get; set; }
	public required List<string> Arguments { get; set; }
	public required Dictionary<string, string> Env { get; set; }
}
