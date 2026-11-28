namespace SharpDbg.Infrastructure.Debugger;

public partial class ManagedDebugger
{
	/// <summary>Distinct source files known from the loaded modules' symbols, for the loadedSources request.</summary>
	public IReadOnlyList<string> GetLoadedSources()
	{
		var result = new List<string>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var module in _modules.Values)
		{
			var reader = module.MetadataReader;
			if (reader is null) continue;
			foreach (var path in reader.GetSourceFiles())
			{
				if (!string.IsNullOrEmpty(path) && seen.Add(path)) result.Add(path);
			}
		}
		return result;
	}
}
