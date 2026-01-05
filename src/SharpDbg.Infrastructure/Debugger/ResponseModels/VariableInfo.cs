namespace SharpDbg.Infrastructure.Debugger.ResponseModels;

public class VariableInfo
{
	public required string Name { get; set; }
	public required string Value { get; set; }
	public required string? Type { get; set; }
	public required int VariablesReference { get; set; }
	public VariablePresentationHint? PresentationHint { get; set; }
}
