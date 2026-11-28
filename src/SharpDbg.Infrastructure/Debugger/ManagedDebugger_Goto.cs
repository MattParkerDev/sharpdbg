using ICorDebugSharp;

namespace SharpDbg.Infrastructure.Debugger;

public partial class ManagedDebugger
{
	/// <summary>Positions on the requested line where execution can be moved to (gotoTargets).</summary>
	public IReadOnlyList<(int Line, int Column, int EndLine, int EndColumn)> GetGotoTargets(string filePath, int line, int? column)
	{
		var result = new List<(int Line, int Column, int EndLine, int EndColumn)>();
		var seen = new HashSet<(int, int, int, int)>();
		foreach (var module in _modules.Values)
		{
			var reader = module.MetadataReader;
			if (reader is null) continue;
			if (reader.FindDocument(filePath) is not { } document) continue;
			if (reader.ResolveBreakpoint(document, line, column) is not { } resolved) continue;
			if (seen.Add((resolved.StartLine, resolved.StartColumn, resolved.EndLine, resolved.EndColumn)))
				result.Add((resolved.StartLine, resolved.StartColumn, resolved.EndLine, resolved.EndColumn));
		}
		return result;
	}

	/// <summary>Moves the active frame's instruction pointer to the statement at the given source position
	/// (DAP goto / "Set Next Statement"). Returns the new location.</summary>
	public (int Line, int Column) SetNextStatement(int threadIdInt, string filePath, int line, int? column)
	{
		if (!_threads.TryGetValue(threadIdInt, out var threadInfo))
			throw new InvalidOperationException($"Thread {threadIdInt} does not exist.");
		var frame = threadInfo.Thread.ActiveFrame;
		if (frame is not ICorDebugILFrame ilFrame)
			throw new InvalidOperationException("The active frame is not an IL frame.");
		var module = _modules[ilFrame.Function.Module.BaseAddress];
		var document = module.MetadataReader.FindDocument(filePath)
			?? throw new InvalidOperationException($"No source document for {filePath}.");
		var resolved = module.MetadataReader.ResolveBreakpoint(document, line, column)
			?? throw new InvalidOperationException($"No statement at {filePath}:{line}.");
		if (resolved.MethodToken != ilFrame.Function.Token)
			throw new InvalidOperationException("The target statement is not in the active frame's method.");
		ilFrame.SetIP(resolved.ILOffset);
		return (resolved.StartLine, resolved.StartColumn);
	}
}
