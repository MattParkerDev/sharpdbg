using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using ICorDebugSharp;
using ZLinq;

namespace SharpDbg.Infrastructure.Debugger;

/// <summary>
/// Reads module metadata and, when available, portable PDB metadata.
/// </summary>
public partial class ModuleMetadataReader : IDisposable
{
	private readonly PEReader _peReader;
	private readonly MetadataReader _peMetadataReader;
	private MetadataReaderProvider? _pdbProvider;
	private MetadataReader? _pdbMetadataReader;
	internal MetadataReader PeMetadataReader => _peMetadataReader;
	internal MetadataReader? PdbMetadataReader => _pdbMetadataReader;
	private Guid? _mvid;
	internal Guid Mvid => _mvid ??= _peMetadataReader.GetGuid(_peMetadataReader.GetModuleDefinition().Mvid);
	public bool HasSymbols => _pdbMetadataReader is not null;

	/// Lines and columns are 1 based
	public record ResolvedBreakpoint(
		int MethodToken,
		int ILOffset,
		int StartLine,
		int EndLine,
		int StartColumn,
		int EndColumn,
		string DocumentPath
	);

	/// <summary>
	/// Information about an await block in an async method
	/// </summary>
	public record AsyncAwaitInfo(uint YieldOffset, uint ResumeOffset);

	/// <summary>
	/// Complete async method stepping information
	/// </summary>
	public class AsyncMethodSteppingInfo
	{
		public List<AsyncAwaitInfo> AwaitInfos { get; set; } = new();
		public int LastUserCodeIlOffset { get; set; }
	}

	private ModuleMetadataReader(PEReader peReader)
	{
		_peReader = peReader;
		_peMetadataReader = peReader.GetMetadataReader(MetadataReaderOptions.None);
	}

	/// <summary>
	/// Try to load module metadata and any matching portable PDB.
	/// </summary>
	/// <param name="assemblyPath">Path to the assembly (.dll)</param>
	/// <returns>A reader for a valid managed PE, whether or not symbols were found.</returns>
	public static ModuleMetadataReader? TryLoad(string assemblyPath)
	{
		if (!File.Exists(assemblyPath)) return null;
		try
		{
			using var stream = File.OpenRead(assemblyPath);
			return TryLoadInternal(stream, assemblyPath);
		}
		catch { return null; }
	}

	public bool TryLoadSymbols(string pdbPath)
	{
		if (!File.Exists(pdbPath)) return false;
		MetadataReaderProvider? provider = null;
		try
		{
			provider = MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(pdbPath), MetadataStreamOptions.PrefetchMetadata);
			SetPdbProvider(provider);
			return true;
		}
		catch
		{
			provider?.Dispose();
			return false;
		}
	}

	public static ModuleMetadataReader? TryLoadFromBytes(byte[] inMemoryModuleBytes)
	{
		try
		{
			using var stream = new MemoryStream(inMemoryModuleBytes, writable: false);
			return TryLoadInternal(stream);
		}
		catch
		{
			return null;
		}
	}

	private static ModuleMetadataReader? TryLoadInternal(Stream assemblyStream, string? assemblyPath = null)
	{
		PEReader? peReader = null;
		try
		{
			peReader = new PEReader(assemblyStream, PEStreamOptions.PrefetchEntireImage);
			var result = new ModuleMetadataReader(peReader);

			// Look for debug directory entries
			DebugDirectoryEntry codeViewEntry = default;
			DebugDirectoryEntry embeddedPdbEntry = default;

			foreach (var entry in peReader.ReadDebugDirectory())
			{
				if (entry.Type == DebugDirectoryEntryType.CodeView)
				{
					// Check for Portable PDB magic number
					const ushort PortableCodeViewVersionMagic = 0x504d;
					if (entry.MinorVersion == PortableCodeViewVersionMagic)
					{
						codeViewEntry = entry;
					}
				}
				else if (entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb)
				{
					embeddedPdbEntry = entry;
				}
			}

			// Try CodeView (external PDB file) first
			if (codeViewEntry.DataSize != 0)
			{
				result.TryLoadFromCodeView(codeViewEntry, assemblyPath);
			}

			// Try embedded PDB
			if (embeddedPdbEntry.DataSize != 0)
			{
				result.TryLoadEmbeddedPdb(embeddedPdbEntry);
			}

			return result;
		}
		catch
		{
			peReader?.Dispose();
			return null;
		}
	}

	private bool TryLoadFromCodeView(DebugDirectoryEntry codeViewEntry, string? assemblyPath)
	{
		MetadataReaderProvider? provider = null;
		try
		{
			var codeViewData = _peReader.ReadCodeViewDebugDirectoryData(codeViewEntry);
			var pdbPath = codeViewData.Path;

			// Try PDB in same directory as assembly
			var assemblyDir = Path.GetDirectoryName(assemblyPath);
			if (assemblyDir is not null)
			{
				var pdbFileName = Path.GetFileName(pdbPath);
				pdbPath = Path.Combine(assemblyDir, pdbFileName);
			}

			if (!File.Exists(pdbPath))
				return false;

			// Don't need to dispose stream, FromPortablePdbStream disposes of it internally
			var pdbStream = File.OpenRead(pdbPath);
			provider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
			var reader = provider.GetMetadataReader();

			// Validate PDB matches assembly
			var pdbId = new BlobContentId(reader.DebugMetadataHeader!.Id);
			var expectedId = new BlobContentId(codeViewData.Guid, codeViewEntry.Stamp);

			if (codeViewData.Age == 1 && pdbId == expectedId)
			{
				SetPdbProvider(provider);
				return true;
			}

			// PDB doesn't match, dispose and return null
			provider.Dispose();
			return false;
		}
		catch
		{
			provider?.Dispose();
			return false;
		}
	}

	private bool TryLoadEmbeddedPdb(DebugDirectoryEntry embeddedPdbEntry)
	{
		try
		{
			SetPdbProvider(_peReader.ReadEmbeddedPortablePdbDebugDirectoryData(embeddedPdbEntry));
			return true;
		}
		catch
		{
			return false;
		}
	}

	private void SetPdbProvider(MetadataReaderProvider provider)
	{
		var reader = provider.GetMetadataReader();
		_pdbProvider?.Dispose();
		_pdbProvider = provider;
		_pdbMetadataReader = reader;
	}

	public (string sourceFilePath, int startLine, int endLine, int startColumn, int endColumn)? GetSourceLocationForOffset(int methodToken, int ilOffset)
	{
		var reader = _pdbMetadataReader;
		if (reader is null) return null;
		var methodHandle = MetadataTokens.MethodDefinitionHandle(methodToken);
		var methodDebugInfo = reader.GetMethodDebugInformation(methodHandle);

		if (methodDebugInfo.SequencePointsBlob.IsNil)
			return null;

		var points = methodDebugInfo.GetSequencePoints()
			.AsValueEnumerable()
			.Where(sp => sp.IsHidden is false)
			.ToList();

		// Ideally we find an exact match
		var sequencePoint = points
			.AsValueEnumerable()
			.Where(sp => sp.Offset == ilOffset)
			.Cast<SequencePoint?>()
			.SingleOrDefault();

		// e.g. when stepping at the end of a method, there may be no exact match - find the closest prior sequence point of the il offset
		sequencePoint ??= points
			.AsValueEnumerable()
			.Where(sp => sp.Offset < ilOffset)
			.OrderByDescending(sp => sp.Offset)
			.Cast<SequencePoint?>()
			.FirstOrDefault();

		if (sequencePoint is null) return null;
		var sp = sequencePoint.Value;

		var spDocument = sp.Document.IsNil ? methodDebugInfo.Document : sp.Document;
		var document = reader.GetDocument(spDocument);
		var documentFilePath = reader.GetString(document.Name);
		return (documentFilePath, sp.StartLine, sp.EndLine, sp.StartColumn, sp.EndColumn);
	}

	internal ResolvedBreakpoint? ResolveBreakpointAtMethodEntry(int methodToken)
	{
		var reader = _pdbMetadataReader;
		if (reader is null) return null;
		var methodHandle = MetadataTokens.MethodDefinitionHandle(methodToken);
		var methodDebugInfo = reader.GetMethodDebugInformation(methodHandle);
		var sequencePoint = methodDebugInfo.GetSequencePoints().FirstOrDefault(sp => !sp.IsHidden);
		if (sequencePoint.Document.IsNil && methodDebugInfo.Document.IsNil) return null;
		var documentHandle = sequencePoint.Document.IsNil ? methodDebugInfo.Document : sequencePoint.Document;
		var document = reader.GetDocument(documentHandle);
		return new ResolvedBreakpoint(methodToken, sequencePoint.Offset, sequencePoint.StartLine, sequencePoint.EndLine,
			sequencePoint.StartColumn, sequencePoint.EndColumn, reader.GetString(document.Name));
	}

	public ImmutableArray<string> GetImportedNamespaces(int methodToken)
	{
		var handle = MetadataTokens.Handle(methodToken);
		if (handle.Kind is not HandleKind.MethodDefinition) throw new ArgumentException("methodToken is not a valid MethodDefinition token");
		var methodHandle = (MethodDefinitionHandle)handle;
		var methodDebugHandle = methodHandle.ToDebugInformationHandle();
		var namespaces = ImmutableArray.CreateBuilder<string>();

		var reader = _pdbMetadataReader;
		if (reader is not null)
		{
			foreach (var scopeHandle in reader.GetLocalScopes(methodDebugHandle))
			{
				var scope = reader.GetLocalScope(scopeHandle);
				var importScope = reader.GetImportScope(scope.ImportScope);
				foreach (var import in importScope.GetImports())
				{
					if (import.Kind == ImportDefinitionKind.ImportNamespace)
					{
						var blobReader = reader.GetBlobReader(import.TargetNamespace);
						var namespaceName = blobReader.ReadUTF8(blobReader.Length);
						namespaces.Add(namespaceName);
					}
				}
			}
		}
		// TODO: I wonder if it is faster to pass a class token of the containing class from the metadata side rather than looking it up here
		var methodDef = _peMetadataReader.GetMethodDefinition(methodHandle);
		var typeDef = methodDef.GetDeclaringType();
		var typeDefObj = _peMetadataReader.GetTypeDefinition(typeDef);
		//var typeNamespace = _peMetadataReader.GetNamespaceDefinition(typeDefObj.NamespaceDefinition);
		var typeNamespaceName = _peMetadataReader.GetString(typeDefObj.Namespace);
		if (string.IsNullOrEmpty(typeNamespaceName) is false && namespaces.Contains(typeNamespaceName) is false)
		{
			namespaces.Add(typeNamespaceName);
		}
		namespaces.Add(""); // global namespace

		return namespaces.ToImmutable();
	}

	public string? GetLocalVariableName(int methodToken, int localIndex, int currentIlOffset)
	{
		var reader = _pdbMetadataReader;
		if (reader is null) return null;
		var methodHandle = MetadataTokens.MethodDefinitionHandle(methodToken);

		var localScopes = reader.GetLocalScopes(methodHandle);
		foreach (var scopeHandle in localScopes)
		{
			var scope = reader.GetLocalScope(scopeHandle);

			// Only consider scopes that are active at the current IL offset
			if (currentIlOffset < scope.StartOffset || currentIlOffset >= scope.StartOffset + scope.Length)
				continue;

			foreach (var variableHandle in scope.GetLocalVariables())
			{
				var variable = reader.GetLocalVariable(variableHandle);

				if (variable.Index == localIndex)
				{
					if (variable.Attributes is LocalVariableAttributes.DebuggerHidden) return "HIDDEN";
					if (variable.Name.IsNil) return "NIL";
					return reader.GetString(variable.Name);
				}
			}
		}

		return null;
	}

	public string? GetArgumentName(int methodToken, int paramIndex)
	{
		var methodHandle = MetadataTokens.MethodDefinitionHandle(methodToken);
		var methodDef = _peMetadataReader.GetMethodDefinition(methodHandle);

		var parameters = methodDef.GetParameters();

		int currentIndex = 0;
		foreach (var paramHandle in parameters)
		{
			if (currentIndex == paramIndex)
			{
				var param = _peMetadataReader.GetParameter(paramHandle);

				if (param.Name.IsNil) return null;
				return _peMetadataReader.GetString(param.Name);
			}
			currentIndex++;
		}

		return null;
	}

	public (int ilStartOffset, int ilEndOffset)? GetStartAndEndSequencePointIlOffsetsForIlOffset(int methodToken, int ip)
	{
		var reader = _pdbMetadataReader;
		if (reader is null) return null;
		var methodHandle = MetadataTokens.MethodDefinitionHandle(methodToken);
		var debugInfo = reader.GetMethodDebugInformation(methodHandle);

		if (debugInfo.SequencePointsBlob.IsNil) return null;

		// Get valid, ordered sequence points
		var points = debugInfo
			.GetSequencePoints()
			.Where(sp => sp.StartLine != 0 && sp.IsHidden is false)
			.OrderBy(sp => sp.Offset)
			.Cast<SequencePoint?>()
			.ToList();

		if (points.Count is 0) return null;

		// Find the last point at or before the IP
		var startPoint = points.LastOrDefault(sp => sp!.Value.Offset <= ip); // e.g. ip = 0, it is possible that there is no matching sequence point

		// Find the first point after the IP
		var endPoint = points.FirstOrDefault(sp => sp!.Value.Offset > ip);

		var ilStartOffset = startPoint?.Offset ?? ip;
		var ilEndOffset = endPoint?.Offset ?? ilStartOffset;

		// Calling method will handle when ilEndOffset == ilStartOffset, and change it to method size
		return (ilStartOffset, ilEndOffset);
	}

	public (int currentIlOffset, int? nextUserCodeIlOffset) GetFrameCurrentIlOffsetAndNextUserCodeIlOffset(ICorDebugILFrame ilFrame)
	{
		var method = ilFrame.Function;
		var code = method.ILCode;
		var methodToken = method.Token;
		var ipResult = ilFrame.IP;
		if (ipResult.pMappingResult is CorDebugMappingResult.MAPPING_UNMAPPED_ADDRESS or CorDebugMappingResult.MAPPING_NO_INFO)
		{
			throw new InvalidOperationException("IL Frame IP is unmapped or has no info");
		}
		var nextUserCodeIlOffset = GetNextUserCodeIlOffset(methodToken, ipResult.pnOffset);
		return (ipResult.pnOffset, nextUserCodeIlOffset);
	}

	public int? GetNextUserCodeIlOffset(int methodToken, int currentIlOffset)
	{
		var reader = _pdbMetadataReader;
		if (reader is null) return null;
		var methodHandle = MetadataTokens.MethodDefinitionHandle(methodToken);
		var debugInfo = reader.GetMethodDebugInformation(methodHandle);
		foreach (var sequencePoint in debugInfo.GetSequencePoints())
		{
			if (sequencePoint.StartLine is 0 or SequencePoint.HiddenLine)
				continue;

			if (sequencePoint.Offset >= currentIlOffset)
			{
				var nextUserCodeIlOffset = sequencePoint.Offset;
				return nextUserCodeIlOffset;
			}
		}
		return null;
	}

	// Guid for async method stepping information from Roslyn
	// https://github.com/dotnet/roslyn/blob/afd10305a37c0ffb2cfb2c2d8446154c68cfa87a/src/Dependencies/CodeAnalysis.Debugging/PortableCustomDebugInfoKinds.cs#L13
	private static readonly Guid _asyncMethodSteppingInformationBlob = new("54FD2AC5-E925-401A-9C2A-F94F171072F8");

	/// <summary>
	/// Get async method stepping information for a method.
	/// This includes await block yield/resume offsets and last user code IL offset.
	/// </summary>
	/// <param name="methodToken">Method token</param>
	/// <returns>Async method stepping info if method has await blocks, null otherwise</returns>
	public AsyncMethodSteppingInfo? GetAsyncMethodSteppingInfo(int methodToken)
	{
		var reader = _pdbMetadataReader;
		if (reader is null) return null;
		var methodHandle = MetadataTokens.MethodDefinitionHandle(methodToken);
		//var methodDebugInfoHandle = methodHandle.ToDebugInformationHandle();
		var entityHandle = MetadataTokens.EntityHandle(methodToken);

		var result = new AsyncMethodSteppingInfo();
		bool foundOffset = false;
		foreach (var cdiHandle in reader.GetCustomDebugInformation(entityHandle))
		{
			var cdi = reader.GetCustomDebugInformation(cdiHandle);

			if (reader.GetGuid(cdi.Kind) == _asyncMethodSteppingInformationBlob)
			{
				var blobReader = reader.GetBlobReader(cdi.Value);

				// Skip catch_handler_offset
				blobReader.ReadUInt32();

				// Read yield_offset, resume_offset, compressed_token tuples
				while (blobReader.Offset < blobReader.Length)
				{
					var yieldOffset = blobReader.ReadUInt32();
					var resumeOffset = blobReader.ReadUInt32();
					var token = (uint)blobReader.ReadCompressedInteger();

					result.AwaitInfos.Add(new AsyncAwaitInfo(yieldOffset, resumeOffset));
				}
			}
		}

		if (result.AwaitInfos.Count == 0)
			return null;

		// Find last IL offset for user code in this method
		var debugInfo = reader.GetMethodDebugInformation(methodHandle);

		if (!debugInfo.SequencePointsBlob.IsNil)
		{
			foreach (var sp in debugInfo.GetSequencePoints())
			{
				// Skip hidden sequence points and invalid lines
				if (sp.StartLine == 0 || sp.IsHidden || sp.Offset < 0)
					continue;

				result.LastUserCodeIlOffset = sp.Offset;
				foundOffset = true;
			}
		}

		if (!foundOffset)
			return null;

		return result;
	}

	/// <summary>
	/// Get all source files referenced in the PDB
	/// </summary>
	public IEnumerable<string> GetSourceFiles()
	{
		var reader = _pdbMetadataReader;
		if (reader is null) yield break;
		foreach (var handle in reader.Documents)
		{
			var document = reader.GetDocument(handle);
			yield return reader.GetString(document.Name);
		}
	}

	private static string NormalizePath(string path)
	{
		// Normalize to forward slashes and lowercase for comparison
		return path.Replace('\\', '/');
	}

	private static bool PathsMatch(string path1, string path2)
	{
		// Normalize both paths
		var normalized1 = NormalizePath(path1);
		var normalized2 = NormalizePath(path2);

		// Try exact match first (case-insensitive on Windows)
		if (string.Equals(normalized1, normalized2, StringComparison.OrdinalIgnoreCase))
			return true;

		// Try matching by filename only if full paths don't match
		// This handles cases where the PDB has a different absolute path
		var fileName1 = Path.GetFileName(normalized1);
		var fileName2 = Path.GetFileName(normalized2);

		if (string.Equals(fileName1, fileName2, StringComparison.OrdinalIgnoreCase))
		{
			// Check if the relative paths match (handle different roots)
			// For now, just match by filename - could be more sophisticated
			return true;
		}

		return false;
	}

	public void Dispose()
	{
		_pdbProvider?.Dispose();
		_peReader.Dispose();
	}
}
