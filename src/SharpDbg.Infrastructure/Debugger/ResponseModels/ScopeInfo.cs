namespace SharpDbg.Infrastructure.Debugger.ResponseModels;

public class ScopeInfo
{
	public required string Name { get; set; }
	public required int VariablesReference { get; set; }
	public required bool Expensive { get; set; }
}
