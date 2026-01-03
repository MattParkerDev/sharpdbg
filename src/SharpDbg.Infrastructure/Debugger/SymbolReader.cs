using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using ZLinq;

namespace SharpDbg.Infrastructure.Debugger;

/// <summary>
/// Reads portable PDB files and resolves source locations to IL offsets
/// </summary>
public class SymbolReader : IDisposable
{
    private readonly MetadataReaderProvider _provider;
    private readonly MetadataReader _reader;
    private readonly MetadataReader _peMetadataReader;
    private string _path;

    /// <summary>
    /// Result of resolving a breakpoint location
    /// </summary>
    public record ResolvedBreakpoint(
        int MethodToken,
        int ILOffset,
        int StartLine,
        int EndLine,
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

    private SymbolReader(MetadataReaderProvider provider, MetadataReader reader, MetadataReader peMetadataReader,
	    string assemblyPath)
    {
        _provider = provider;
        _reader = reader;
        _peMetadataReader = peMetadataReader;
        _path = assemblyPath;
    }

    /// <summary>
    /// Try to load symbols for the given assembly path
    /// </summary>
    /// <param name="assemblyPath">Path to the assembly (.dll)</param>
    /// <returns>SymbolReader if PDB found and loaded, null otherwise</returns>
    public static SymbolReader? TryLoad(string assemblyPath)
    {
        // First, try to load from CodeView entry in PE (gets PDB path and validates GUID match)
        var result = TryLoadFromAssembly(assemblyPath);
        if (result != null)
            return result;

        return null;
    }

    private static SymbolReader? TryLoadFromAssembly(string assemblyPath)
    {
        if (!File.Exists(assemblyPath))
            return null;

        try
        {
            using var peStream = File.OpenRead(assemblyPath);
            // no longer disposing via using, as the PEReader needs to stay alive while using the MetadataReader
            var peReader = new PEReader(peStream);

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
                var result = TryLoadFromCodeView(peReader, codeViewEntry, assemblyPath);
                if (result != null)
                    return result;
            }

            // Try embedded PDB
            if (embeddedPdbEntry.DataSize != 0)
            {
                return TryLoadEmbeddedPdb(peReader, embeddedPdbEntry);
            }
        }
        catch
        {
            // Ignore errors and return null
        }

        return null;
    }

    private static SymbolReader? TryLoadFromCodeView(PEReader peReader, DebugDirectoryEntry codeViewEntry, string assemblyPath)
    {
        try
        {
            var codeViewData = peReader.ReadCodeViewDebugDirectoryData(codeViewEntry);
            var pdbPath = codeViewData.Path;

            // Try PDB in same directory as assembly
            var assemblyDir = Path.GetDirectoryName(assemblyPath);
            if (assemblyDir != null)
            {
                var pdbFileName = Path.GetFileName(pdbPath);
                pdbPath = Path.Combine(assemblyDir, pdbFileName);
            }

            if (!File.Exists(pdbPath))
                return null;

            var pdbStream = File.OpenRead(pdbPath);
            var provider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
            var reader = provider.GetMetadataReader();

            // Validate PDB matches assembly
            var pdbId = new BlobContentId(reader.DebugMetadataHeader!.Id);
            var expectedId = new BlobContentId(codeViewData.Guid, codeViewEntry.Stamp);

            if (codeViewData.Age == 1 && pdbId == expectedId)
            {
				var peMetadataReader = peReader.GetMetadataReader(MetadataReaderOptions.None);
				return new SymbolReader(provider, reader, peMetadataReader, assemblyPath);
            }

            // PDB doesn't match, dispose and return null
            provider.Dispose();
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static SymbolReader? TryLoadEmbeddedPdb(PEReader peReader, DebugDirectoryEntry embeddedPdbEntry)
    {
        try
        {
            var provider = peReader.ReadEmbeddedPortablePdbDebugDirectoryData(embeddedPdbEntry);
            var reader = provider.GetMetadataReader();
            var peMetadataReader = peReader.GetMetadataReader(MetadataReaderOptions.None);
            return new SymbolReader(provider, reader, peMetadataReader, null!);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Try to resolve a breakpoint at the given source location
    /// </summary>
    /// <param name="sourceFilePath">Full path to the source file</param>
    /// <param name="line">1-based line number</param>
    /// <returns>Resolved breakpoint info, or null if no valid sequence point found</returns>
    public ResolvedBreakpoint? ResolveBreakpoint(string sourceFilePath, int line)
    {
        // Normalize path for comparison
        var normalizedPath = NormalizePath(sourceFilePath);

        // Find the document handle for this source file
        DocumentHandle? documentHandle = null;
        foreach (var handle in _reader.Documents)
        {
            var document = _reader.GetDocument(handle);
            var docPath = _reader.GetString(document.Name);
            if (PathsMatch(normalizedPath, docPath))
            {
                documentHandle = handle;
                break;
            }
        }

        if (documentHandle == null)
            return null;

        // Search all methods for sequence points in this document at or after the requested line
        ResolvedBreakpoint? bestMatch = null;

        foreach (var methodDebugInfoHandle in _reader.MethodDebugInformation)
        {
            var methodDebugInfo = _reader.GetMethodDebugInformation(methodDebugInfoHandle);

            // Skip methods without sequence points
            if (methodDebugInfo.SequencePointsBlob.IsNil)
                continue;

            foreach (var sp in methodDebugInfo.GetSequencePoints())
            {
                // Skip hidden sequence points
                if (sp.IsHidden)
                    continue;

                // Check if this sequence point is in the target document
                // Note: sp.Document can be default if it's the same as the method's document
                var spDocument = sp.Document.IsNil ? methodDebugInfo.Document : sp.Document;
                if (spDocument != documentHandle)
                    continue;

                // Check if this sequence point covers or is at/after the requested line
                if (sp.StartLine <= line && sp.EndLine >= line)
                {
                    // Exact match - line is within this sequence point
                    var methodToken = MetadataTokens.GetToken(methodDebugInfoHandle.ToDefinitionHandle());
                    var docPath = _reader.GetString(_reader.GetDocument(spDocument).Name);

                    return new ResolvedBreakpoint(
                        methodToken,
                        sp.Offset,
                        sp.StartLine,
                        sp.EndLine,
                        docPath
                    );
                }
                else if (sp.StartLine > line)
                {
                    // Sequence point is after requested line - could be the next valid location
                    var methodToken = MetadataTokens.GetToken(methodDebugInfoHandle.ToDefinitionHandle());
                    var docPath = _reader.GetString(_reader.GetDocument(spDocument).Name);

                    var candidate = new ResolvedBreakpoint(
                        methodToken,
                        sp.Offset,
                        sp.StartLine,
                        sp.EndLine,
                        docPath
                    );

                    // Keep the closest one after the requested line
                    if (bestMatch == null || sp.StartLine < bestMatch.StartLine)
                    {
                        bestMatch = candidate;
                    }
                }
            }
        }

        return bestMatch;
    }

    public (string sourceFilePath, int startLine, int endLine, int startColumn, int endColumn)? GetSourceLocationForOffset(int methodToken, int ilOffset)
    {
		var methodHandle = MetadataTokens.MethodDefinitionHandle(methodToken);
		var methodDebugInfo = _reader.GetMethodDebugInformation(methodHandle);

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

		if (sequencePoint == null) return null;
		var sp = sequencePoint.Value;

		var spDocument = sp.Document.IsNil ? methodDebugInfo.Document : sp.Document;
		var document = _reader.GetDocument(spDocument);
		var documentFilePath = _reader.GetString(document.Name);
		return (documentFilePath, sp.StartLine, sp.EndLine, sp.StartColumn, sp.EndColumn);
    }

    public ImmutableArray<string> GetImportedNamespaces(int methodToken)
    {
	    var handle = MetadataTokens.Handle(methodToken);
	    if (handle.Kind is not HandleKind.MethodDefinition) throw new ArgumentException("methodToken is not a valid MethodDefinition token");
	    var methodHandle = (MethodDefinitionHandle)handle;
	    var methodDebugHandle = methodHandle.ToDebugInformationHandle();
	    var namespaces = ImmutableArray.CreateBuilder<string>();

	    var localScopes = _reader.GetLocalScopes(methodDebugHandle);
	    foreach (var scopeHandle in localScopes)
	    {
		    var scope = _reader.GetLocalScope(scopeHandle);
		    var importScope = _reader.GetImportScope(scope.ImportScope);
		    foreach (var import in importScope.GetImports())
		    {
			    if (import.Kind == ImportDefinitionKind.ImportNamespace)
			    {
				    var blobReader = _reader.GetBlobReader(import.TargetNamespace);
				    var namespaceName = blobReader.ReadUTF8(blobReader.Length);
				    namespaces.Add(namespaceName);
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

    public string? GetLocalVariableName(int methodToken, int localIndex)
    {
	    var methodHandle = MetadataTokens.MethodDefinitionHandle(methodToken);

	    var localScopes = _reader.GetLocalScopes(methodHandle);
	    foreach (var scopeHandle in localScopes)
	    {
		    var scope = _reader.GetLocalScope(scopeHandle);

		    foreach (var variableHandle in scope.GetLocalVariables())
		    {
			    var variable = _reader.GetLocalVariable(variableHandle);

			    if (variable.Index == localIndex)
			    {
				    if (variable.Attributes is LocalVariableAttributes.DebuggerHidden) return "HIDDEN";
				    if (variable.Name.IsNil) return "NIL";
				    return _reader.GetString(variable.Name);
			    }
		    }
	    }

	    return null;
    }

    public string? GetArgumentName(int methodToken, int paramIndex)
    {
	    var methodHandle = MetadataTokens. MethodDefinitionHandle(methodToken);
	    var methodDef = _reader.GetMethodDefinition(methodHandle);

	    var parameters = methodDef.GetParameters();

	    int currentIndex = 0;
	    foreach (var paramHandle in parameters)
	    {
		    if (currentIndex == paramIndex)
		    {
			    var param = _reader.GetParameter(paramHandle);

			    if (param.Name.IsNil) return null;
			    return _reader.GetString(param.Name);
		    }
		    currentIndex++;
	    }

	    return null;
    }

	public (int ilStartOffset, int ilEndOffset) GetStartAndEndSequencePointIlOffsetsForIlOffset(int methodToken, int ip)
	{
		var methodHandle = MetadataTokens.MethodDefinitionHandle(methodToken);
		var debugInfo = _reader.GetMethodDebugInformation(methodHandle);

		if (debugInfo.SequencePointsBlob.IsNil)
			return (0, 0);

		// Get valid, ordered sequence points
		var points = debugInfo
			.GetSequencePoints()
			.Where(sp => sp.StartLine != 0 && sp.IsHidden is false)
			.OrderBy(sp => sp.Offset)
			.Cast<SequencePoint?>()
			.ToList();

		if (points.Count is 0) return (0, 0);

		// Find the last point at or before the IP
		var startPoint = points.LastOrDefault(sp => sp!.Value.Offset <= ip) ?? points[0]!.Value;

		// Find the first point after the IP
		var endPoint = points.FirstOrDefault(sp => sp!.Value.Offset > ip);

		var ilStartOffset = startPoint.Offset;
		var ilEndOffset = endPoint?.Offset ?? ilStartOffset;

		// Calling method will handle when ilEndOffset == ilStartOffset, and change it to method size
		return (ilStartOffset, ilEndOffset);
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
        var methodHandle = MetadataTokens.MethodDefinitionHandle(methodToken);
        //var methodDebugInfoHandle = methodHandle.ToDebugInformationHandle();
        var entityHandle = MetadataTokens.EntityHandle(methodToken);

        var result = new AsyncMethodSteppingInfo();
        bool foundOffset = false;
        foreach (var cdiHandle in _reader.GetCustomDebugInformation(entityHandle))
        {
            var cdi = _reader.GetCustomDebugInformation(cdiHandle);

            if (_reader.GetGuid(cdi.Kind) == _asyncMethodSteppingInformationBlob)
            {
                var blobReader = _reader.GetBlobReader(cdi.Value);

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
        var debugInfo = _reader.GetMethodDebugInformation(methodHandle);

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
        foreach (var handle in _reader.Documents)
        {
            var document = _reader.GetDocument(handle);
            yield return _reader.GetString(document.Name);
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
        _provider.Dispose();
    }
}
