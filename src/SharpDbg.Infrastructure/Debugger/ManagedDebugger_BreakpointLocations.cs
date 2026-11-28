namespace SharpDbg.Infrastructure.Debugger;

public partial class ManagedDebugger
{
	/// <summary>Valid breakpoint positions for a source range, aggregated across loaded modules.</summary>
	public IReadOnlyList<(int Line, int Column, int EndLine, int EndColumn)> GetBreakpointLocations(
		string filePath, int line, int? column, int? endLine, int? endColumn)
	{
		var result = new List<(int Line, int Column, int EndLine, int EndColumn)>();
		foreach (var module in _modules.Values)
		{
			var reader = module.MetadataReader;
			if (reader is null) continue;
			result.AddRange(reader.GetBreakpointLocations(filePath, line, column, endLine, endColumn));
		}
		return result;
	}
}
