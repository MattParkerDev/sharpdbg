namespace SharpDbg.Infrastructure.Debugger;

public partial class ManagedDebugger
{
	/// <summary>The SourceLink URL for a source file, from the first loaded module whose PDB has a mapping.</summary>
	public string? GetSourceLinkUrl(string filePath)
	{
		foreach (var module in _modules.Values)
		{
			var url = module.MetadataReader.GetSourceLinkUrl(filePath);
			if (url is not null) return url;
		}
		return null;
	}
}
