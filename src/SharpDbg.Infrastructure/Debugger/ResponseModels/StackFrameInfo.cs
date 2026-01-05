namespace SharpDbg.Infrastructure.Debugger.ResponseModels;

public class StackFrameInfo
{
	public required int Id { get; set; }
	public required string Name { get; set; }
	public required int Line { get; set; }
	public required int EndLine { get; set; }
	public required int Column { get; set; }
	public required int EndColumn { get; set; }
	public required string? Source { get; set; }
}
