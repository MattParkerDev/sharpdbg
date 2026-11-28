namespace SharpDbg.Infrastructure.Debugger;

public partial class ManagedDebugger
{
	private readonly Dictionary<string, int> _embeddedSourceIds = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<int, string> _embeddedSources = new();
	private int _nextEmbeddedSourceId;

	/// <summary>
	/// Assigns (or returns) the sourceReference a document with embedded source is served under, so the client can
	/// fetch the text through the source request. Zero when this module has no embedded source for the document.
	/// </summary>
	public int GetEmbeddedSourceReference(ModuleInfo module, string sourceFilePath)
	{
		if (!module.MetadataReader.TryGetEmbeddedSource(sourceFilePath, out var text) || text is null)
			return 0;
		if (_embeddedSourceIds.TryGetValue(sourceFilePath, out var existing))
			return existing;
		var id = ++_nextEmbeddedSourceId;
		_embeddedSourceIds[sourceFilePath] = id;
		_embeddedSources[id] = text;
		return id;
	}

	/// <summary>The embedded source text a sourceReference was assigned to.</summary>
	public bool TryGetEmbeddedSourceText(int sourceReference, out string text) => _embeddedSources.TryGetValue(sourceReference, out text!);
}
