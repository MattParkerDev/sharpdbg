using System.Reflection.Metadata;
using System.Text;

namespace SharpDbg.Infrastructure.Debugger;

public partial class ModuleMetadataReader
{
	// The portable PDB custom debug information kinds for embedded source and SourceLink.
	static readonly Guid EmbeddedSourceKind = new("0E8A571B-6926-466E-B4AD-8AB04611F5FE");

	/// <summary>The source text embedded in the PDB for a document, or null when the PDB has none.</summary>
	public bool TryGetEmbeddedSource(string sourceFilePath, out string? sourceText)
	{
		sourceText = null;
		var reader = _pdbMetadataReader;
		if (reader is null) return false;
		if (FindDocument(sourceFilePath) is not { } document) return false;

		foreach (var handle in reader.GetCustomDebugInformation(document.Handle))
		{
			var info = reader.GetCustomDebugInformation(handle);
			if (reader.GetGuid(info.Kind) != EmbeddedSourceKind) continue;
			sourceText = DecodeEmbeddedSource(reader.GetBlobBytes(info.Value));
			return sourceText is not null;
		}
		return false;
	}

	static string? DecodeEmbeddedSource(byte[] blob)
	{
		if (blob.Length < 4) return null;
		var uncompressedSize = BitConverter.ToInt32(blob, 0);
		if (uncompressedSize == 0)
			return Encoding.UTF8.GetString(blob, 4, blob.Length - 4);
		try
		{
			using var input = new MemoryStream(blob, 4, blob.Length - 4);
			using var deflate = new System.IO.Compression.DeflateStream(input, System.IO.Compression.CompressionMode.Decompress);
			using var output = new MemoryStream();
			deflate.CopyTo(output);
			return Encoding.UTF8.GetString(output.ToArray());
		}
		catch
		{
			return null;
		}
	}
}
