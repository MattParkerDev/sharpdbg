namespace SharpDbg.Application.Terminal;

public class TerminalLaunchRequest
{
	public required string Program { get; set; }
	public required List<string> Arguments { get; set; }
	public required string? WorkingDirectory { get; set; }
	public required Dictionary<string, string> Environment { get; set; }
}

public class TerminalLaunchResponse
{
	public int? ProcessId { get; set; }
	public string? Error { get; set; }
}
