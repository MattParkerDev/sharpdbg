using System.Reflection.Metadata;
using System.Text;

namespace SharpDbg.Infrastructure.Debugger;

public partial class ModuleMetadataReader
{
	// The portable PDB custom debug information kind for the SourceLink document.
	static readonly Guid SourceLinkKind = new("CC110556-A091-4D38-9FEC-25AB9A351A6A");

	/// <summary>The SourceLink JSON document stored in the PDB, or null when the PDB has none.</summary>
	public string? GetSourceLinkJson()
	{
		var reader = _pdbMetadataReader;
		if (reader is null) return null;
		foreach (var handle in reader.GetCustomDebugInformation(EntityHandle.ModuleDefinition))
		{
			var info = reader.GetCustomDebugInformation(handle);
			if (reader.GetGuid(info.Kind) != SourceLinkKind) continue;
			return Encoding.UTF8.GetString(reader.GetBlobBytes(info.Value));
		}
		return null;
	}

	/// <summary>The URL the SourceLink document maps a source path to, or null when no pattern matches.</summary>
	public string? GetSourceLinkUrl(string sourceFilePath)
	{
		var json = GetSourceLinkJson();
		if (json is null) return null;
		try
		{
			using var document = System.Text.Json.JsonDocument.Parse(json);
			if (!document.RootElement.TryGetProperty("documents", out var documents)) return null;
			foreach (var property in documents.EnumerateObject())
			{
				var pattern = property.Name;
				var wildcard = pattern.IndexOf('*');
				if (wildcard < 0)
				{
					if (string.Equals(pattern, sourceFilePath, StringComparison.OrdinalIgnoreCase)) return property.Value.GetString();
					continue;
				}
				var prefix = pattern[..wildcard];
				var suffix = pattern[(wildcard + 1)..];
				if (sourceFilePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && sourceFilePath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
				{
					var middle = sourceFilePath.Substring(prefix.Length, sourceFilePath.Length - prefix.Length - suffix.Length);
					return property.Value.GetString()?.Replace("*", middle);
				}
			}
		}
		catch
		{
			// a malformed SourceLink document simply yields no mapping
		}
		return null;
	}
}
