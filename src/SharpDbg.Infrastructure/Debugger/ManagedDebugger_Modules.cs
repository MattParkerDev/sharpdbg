namespace SharpDbg.Infrastructure.Debugger;

/// <summary>One loaded module, as the DAP modules request reports it.</summary>
public sealed record ModuleDescriptor(string Id, string Name, string Path, bool IsUserCode, bool HasSymbols);

public partial class ManagedDebugger
{
	/// <summary>The loaded modules in a stable order, for the DAP modules request.</summary>
	public IReadOnlyList<ModuleDescriptor> GetModules()
	{
		return _modules.Values
			.OrderBy(module => module.ModulePath, StringComparer.Ordinal)
			.Select(module => new ModuleDescriptor(
				module.ModulePath,
				module.ModuleName,
				module.ModulePath,
				module.IsUserCode,
				module.MetadataReader.HasSymbols))
			.ToList();
	}
}
