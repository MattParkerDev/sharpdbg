using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Ardalis.GuardClauses;
using ICorDebugSharp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.ExpressionEvaluator;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Symbols.Metadata.PE;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.ExpressionEvaluator;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.VisualStudio.Debugger.Evaluation;
using Microsoft.VisualStudio.Debugger.Clr;
using SharpDbg.Infrastructure.Debugger.ExpressionEvaluator.Cil;

namespace SharpDbg.Infrastructure.Debugger.ExpressionEvaluator.Compiler;

internal sealed class CompiledEvaluationMethod : IDisposable
{
	private readonly MemoryStream _peStream;
	private readonly Dictionary<MethodDefinitionHandle, DecodedMethod> _decodedMethods = new();

	public CompiledEvaluationMethod(byte[] assembly, string typeName, string methodName)
	{
		Assembly = assembly;
		TypeName = typeName;
		MethodName = methodName;
		_peStream = new MemoryStream(assembly, writable: false);
		PeReader = new System.Reflection.PortableExecutable.PEReader(_peStream);
		MetadataReader = PeReader.GetMetadataReader();
		EntryMethod = FindEntryMethod(MetadataReader, typeName, methodName);
	}

	public byte[] Assembly { get; }
	public string TypeName { get; }
	public string MethodName { get; }
	public System.Reflection.PortableExecutable.PEReader PeReader { get; }
	public MetadataReader MetadataReader { get; }
	public MethodDefinitionHandle EntryMethod { get; }

	/// <summary>
	/// Returns the decoded CIL instructions and branch-target offsets map for a method in the evaluation assembly,
	/// decoding at most once per method body since the assembly is immutable for the lifetime of this object.
	/// </summary>
	public DecodedMethod GetDecodedMethod(MethodDefinitionHandle handle)
	{
		if (_decodedMethods.TryGetValue(handle, out var decoded)) return decoded;
		var method = MetadataReader.GetMethodDefinition(handle);
		var body = PeReader.GetMethodBody(method.RelativeVirtualAddress);
		var ilBytes = body.GetILBytes();
		Guard.Against.Null(ilBytes);
		var instructions = CilInstructionDecoder.Decode(ilBytes);
		var offsets = instructions.Select((instruction, index) => (instruction.Offset, index)).ToDictionary(x => x.Offset, x => x.index);
		decoded = new DecodedMethod(instructions, offsets);
		_decodedMethods[handle] = decoded;
		return decoded;
	}

	public void Dispose()
	{
		PeReader.Dispose();
		_peStream.Dispose();
	}

	private static MethodDefinitionHandle FindEntryMethod(MetadataReader reader, string typeName, string methodName)
	{
		foreach (var typeHandle in reader.TypeDefinitions)
		{
			var type = reader.GetTypeDefinition(typeHandle);
			if (reader.GetString(type.Name) != typeName) continue;

			foreach (var methodHandle in type.GetMethods())
			{
				if (reader.GetString(reader.GetMethodDefinition(methodHandle).Name) == methodName)
				{
					return methodHandle;
				}
			}
		}

		throw new InvalidOperationException($"Generated evaluation method '{typeName}.{methodName}' was not found");
	}
}

// 🤖
internal sealed class CilExpressionCompiler(ManagedDebugger debugger)
{
	private static readonly PortableExecutableReference _intrinsicMethodsReference = MetadataReference.CreateFromImage(CreateIntrinsicMethodsAssembly());

	private const int CacheCapacity = 256;
	private readonly record struct CompileCacheKey(int ModuleVersion, CompileContextKind ContextKind, CORDB_ADDRESS Module, int Token, int IlOffset, string Expression, bool HasException);
	private enum CompileContextKind { Method, Type }
	private readonly Dictionary<CompileCacheKey, CacheEntry> _compileCache = new();
	private readonly LinkedList<CompileCacheKey> _compileLru = new();
	private readonly record struct CacheEntry(CompiledEvaluationMethod Value, LinkedListNode<CompileCacheKey> Node);

	private readonly record struct MetadataBlocksKey(int ModuleVersion, CORDB_ADDRESS PreferredModule);
	private readonly Dictionary<MetadataBlocksKey, ImmutableArray<MetadataBlock>> _metadataBlocksCache = new();
	private int _cachedModuleVersion = -1;

	public CompiledEvaluationMethod? TryCompile(string expression, CompiledExpressionEvaluationContext context, out string? errorMessage)
	{
		errorMessage = null;
		if (context.RootValue is not null)
		{
			var syntax = SyntaxFactory.ParseExpression(expression);
			expression = new RemoveFormatSpecifierAlignmentRewriter().Visit(syntax)!.ToFullString();
		}
		var frame = debugger.GetFrameForThreadIdAndStackDepth(context.ThreadId, context.StackDepth);
		var preferredModule = context.RootValue is not null ? context.RootValue.ExactType.Class.Module : frame.Function.Module;
		var hasException = debugger.GetCurrentException(context.ThreadId) is not null;
		EnsureModuleVersion();
		var key = CreateCacheKey(expression, context, frame, preferredModule, hasException);
		if (TryGetCachedCompiled(key, out var cached)) return cached;

		var blocks = GetMetadataBlocks(preferredModule);
		var evaluationContext = context.RootValue is null
			? CreateMethodContext(blocks, frame)
			: CreateTypeContext(blocks, context.RootValue);

		var diagnostics = DiagnosticBag.GetInstance();
		try
		{
			var aliases = GetAliases(hasException);
			var result = evaluationContext.CompileExpression(
				expression,
				DkmEvaluationFlags.TreatAsExpression,
				aliases,
				diagnostics,
				out _,
				testData: null);

			if (result is null || diagnostics.HasAnyErrors())
			{
				errorMessage = string.Join("; ", diagnostics.AsEnumerable()
					.Where(d => d.Severity == DiagnosticSeverity.Error)
					.Select(d => d.GetMessage()));
				return null;
			}

			return CacheCompiled(key, new CompiledEvaluationMethod(result.Assembly, result.TypeName, result.MethodName));
		}
		finally
		{
			diagnostics.Free();
		}
	}

	private CompileCacheKey CreateCacheKey(string expression, CompiledExpressionEvaluationContext context, ICorDebugILFrame frame, ICorDebugModule preferredModule, bool hasException)
	{
		if (context.RootValue is not null)
		{
			return new CompileCacheKey(
				debugger.ModuleSet_Version,
				CompileContextKind.Type,
				preferredModule.BaseAddress,
				context.RootValue.ExactType.Class.Token,
				0,
				expression,
				hasException);
		}
		return new CompileCacheKey(
			debugger.ModuleSet_Version,
			CompileContextKind.Method,
			preferredModule.BaseAddress,
			frame.Function.Token,
			EvaluationContextBase.NormalizeILOffset((uint)frame.IP.pnOffset),
			expression,
			hasException);
	}

	private bool TryGetCachedCompiled(CompileCacheKey key, out CompiledEvaluationMethod result)
	{
		if (_compileCache.TryGetValue(key, out var entry))
		{
			_compileLru.Remove(entry.Node);
			_compileLru.AddFirst(entry.Node);
			result = entry.Value;
			return true;
		}
		result = null!;
		return false;
	}

	private CompiledEvaluationMethod CacheCompiled(CompileCacheKey key, CompiledEvaluationMethod compiled)
	{
		if (_compileCache.Count >= CacheCapacity)
		{
			var evictedKey = _compileLru.Last!;
			_compileLru.RemoveLast();
			if (_compileCache.Remove(evictedKey.Value, out var evicted)) evicted.Value.Dispose();
		}
		_compileCache[key] = new CacheEntry(compiled, _compileLru.AddFirst(key));
		return compiled;
	}

	private void EnsureModuleVersion()
	{
		if (debugger.ModuleSet_Version == _cachedModuleVersion) return;
		_cachedModuleVersion = debugger.ModuleSet_Version;
		foreach (var entry in _compileCache.Values) entry.Value.Dispose();
		_compileCache.Clear();
		_compileLru.Clear();
		_metadataBlocksCache.Clear();
	}

	private static ImmutableArray<Alias> GetAliases(bool hasException) => hasException
		? [new Alias(DkmClrAliasKind.Exception, "Error", "$exception", typeof(Exception).AssemblyQualifiedName!, Guid.Empty, null!)]
		: ImmutableArray<Alias>.Empty;

	/// <summary>
	/// Builds the metadata blocks passed to the Roslyn evaluator. When multiple loaded modules share an assembly
	/// identity (e.g. the same assembly loaded into several AssemblyLoadContexts), only one module per identity is
	/// kept so that Roslyn binds types against a single well-defined instance. The module the evaluation binds
	/// against (the current frame's module for frame evaluations, or the root value's module for DebuggerDisplay
	/// evaluations) is preferred, since expression semantics (and the tokens emitted into the evaluation assembly)
	/// must match the instance the user is actually debugging.
	/// </summary>
	private ImmutableArray<MetadataBlock> GetMetadataBlocks(ICorDebugModule preferredModule)
	{
		var key = new MetadataBlocksKey(debugger.ModuleSet_Version, preferredModule.BaseAddress);
		if (_metadataBlocksCache.TryGetValue(key, out var cached)) return cached;
		var blocks = BuildMetadataBlocks(preferredModule);
		_metadataBlocksCache[key] = blocks;
		return blocks;
	}

	private ImmutableArray<MetadataBlock> BuildMetadataBlocks(ICorDebugModule preferredModule)
	{
		var modules = GetMetadataModulesPreferringModule(preferredModule);
		var builder = ImmutableArray.CreateBuilder<MetadataBlock>(modules.Count);
		foreach (var moduleInfo in modules)
		{
			var tables = moduleInfo.Module.GetMetaDataInterface<IMetaDataTables2>();
			var (pointer, size) = tables.MetaDataStorage;
			var reader = moduleInfo.MetadataReader.PeMetadataReader;
			var module = reader.GetModuleDefinition();
			var generationId = module.GenerationId.IsNil ? Guid.Empty : reader.GetGuid(module.GenerationId);
			builder.Add(new MetadataBlock(
				new ModuleId(moduleInfo.MetadataReader.Mvid, moduleInfo.ModuleName),
				generationId,
				pointer,
				checked((int)size)));
		}
		return builder.ToImmutable();
	}

	private List<ModuleInfo> GetMetadataModulesPreferringModule(ICorDebugModule preferredModule)
	{
		var modulesByKey = new Dictionary<string, List<ModuleInfo>>(StringComparer.Ordinal);
		foreach (var moduleInfo in debugger.AllModules)
		{
			var key = GetAssemblyIdentityKey(moduleInfo);
			if (!modulesByKey.TryGetValue(key, out var list)) modulesByKey[key] = list = [];
			list.Add(moduleInfo);
		}

		var result = new List<ModuleInfo>(modulesByKey.Count);
		foreach (var group in modulesByKey.Values)
		{
			result.Add(group.FirstOrDefault(module => module.Module == preferredModule) ?? group[0]);
		}
		return result;
	}

	private static string GetAssemblyIdentityKey(ModuleInfo moduleInfo)
	{
		var reader = moduleInfo.MetadataReader.PeMetadataReader;
		if (!reader.IsAssembly) return $"{moduleInfo.ModuleName}|{moduleInfo.MetadataReader.Mvid}";
		var assembly = reader.GetAssemblyDefinition();
		var culture = reader.GetString(assembly.Culture);
		var publicKey = assembly.PublicKey.IsNil ? string.Empty : ToHexString(reader.GetBlobBytes(assembly.PublicKey));
		return $"{reader.GetString(assembly.Name)}|{assembly.Version}|{culture}|{publicKey}";
	}

	private static string ToHexString(byte[] bytes) => Convert.ToHexString(bytes);

	private EvaluationContext CreateMethodContext(ImmutableArray<MetadataBlock> blocks, ICorDebugILFrame frame)
	{
		var moduleInfo = debugger.GetModuleInfoForModule(frame.Function.Module);
		var moduleId = new ModuleId(moduleInfo.MetadataReader.Mvid, moduleInfo.ModuleName);
		var compilation = blocks.ToCompilation(moduleId: default, MakeAssemblyReferencesKind.AllAssemblies).AddReferences(_intrinsicMethodsReference);
		var methodToken = frame.Function.Token;
		var methodHandle = (MethodDefinitionHandle)MetadataTokens.Handle(methodToken);
		var currentSourceMethod = compilation.GetSourceMethod(moduleId, methodHandle);
		var currentFrame = compilation.GetMethod(moduleId, methodHandle);
		var symbolProvider = new CSharpEESymbolProvider(compilation.SourceAssembly, (PEModuleSymbol)currentFrame.ContainingModule, currentFrame);
		var metadataDecoder = new MetadataDecoder((PEModuleSymbol)currentFrame.ContainingModule, currentFrame);
		var localSignature = frame.Function.LocalVarSigToken == 0
			? default
			: (StandaloneSignatureHandle)MetadataTokens.Handle(frame.Function.LocalVarSigToken);
		var localInfo = metadataDecoder.GetLocalInfo(localSignature);
		var ilOffset = EvaluationContextBase.NormalizeILOffset((uint)frame.IP.pnOffset);
		var pdbReader = moduleInfo.MetadataReader.PdbMetadataReader;
		var debugInfo = pdbReader is null
			? MethodDebugInfo<TypeSymbol, LocalSymbol>.None
			: MethodDebugInfo<TypeSymbol, LocalSymbol>.ReadFromPortable(pdbReader, methodToken, ilOffset, symbolProvider, isVisualBasicMethod: false);
		var reuseSpan = debugInfo.ReuseSpan;
		var locals = ArrayBuilder<LocalSymbol>.GetInstance();
		MethodDebugInfo<TypeSymbol, LocalSymbol>.GetLocals(
			locals,
			symbolProvider,
			debugInfo.LocalVariableNames,
			localInfo,
			debugInfo.DynamicLocalMap,
			debugInfo.TupleLocalMap);
		var hoistedLocals = debugInfo.GetInScopeHoistedLocalIndices(ilOffset, ref reuseSpan);
		locals.AddRange(debugInfo.LocalConstants);

		return new EvaluationContext(
			new MethodContextReuseConstraints(moduleId, methodToken, methodVersion: 1, reuseSpan),
			compilation,
			currentFrame,
			currentSourceMethod,
			locals.ToImmutableAndFree(),
			hoistedLocals,
			debugInfo);
	}

	private EvaluationContext CreateTypeContext(ImmutableArray<MetadataBlock> blocks, ICorDebugValue rootValue)
	{
		var rootType = rootValue.ExactType;
		var moduleInfo = debugger.GetModuleInfoForModule(rootType.Class.Module);
		var moduleId = new ModuleId(moduleInfo.MetadataReader.Mvid, moduleInfo.ModuleName);
		var compilation = blocks.ToCompilation(moduleId: default, MakeAssemblyReferencesKind.AllAssemblies).AddReferences(_intrinsicMethodsReference);
		var currentType = compilation.GetType(moduleId, rootType.Class.Token);
		var currentFrame = new SynthesizedContextMethodSymbol(currentType);
		return new EvaluationContext(
			null,
			compilation,
			currentFrame,
			currentSourceMethod: null,
			locals: default,
			inScopeHoistedLocalSlots: ImmutableSortedSet<int>.Empty,
			methodDebugInfo: MethodDebugInfo<TypeSymbol, LocalSymbol>.None);
	}

	private static ImmutableArray<byte> CreateIntrinsicMethodsAssembly()
	{
		const string source = """
			namespace Microsoft.VisualStudio.Debugger.Clr;

			public static class IntrinsicMethods
			{
				public static void CreateVariable(System.Type type, string name, System.Guid customTypeInfoPayloadTypeId, byte[] customTypeInfoPayload) { }
				public static object GetObjectByAlias(string name) => throw null;
				public static ref T GetVariableAddress<T>(string name) => throw null;
				public static System.Exception GetException() => throw null;
			}
			""";
		var compilation = CSharpCompilation.Create(
			"SharpDbg.DebuggerIntrinsics",
			[SyntaxFactory.ParseSyntaxTree(source)],
			[MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
		using var stream = new MemoryStream();
		var result = compilation.Emit(stream);
		if (!result.Success)
		{
			throw new InvalidOperationException(string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.GetMessage())));
		}
		return ImmutableArray.Create(stream.ToArray());
	}

	// To allow us to evaluate DebuggerDisplay attributes that contain nq
	private sealed class RemoveFormatSpecifierAlignmentRewriter : CSharpSyntaxRewriter
	{
		public override SyntaxNode? VisitInterpolation(InterpolationSyntax node)
		{
			var result = (InterpolationSyntax)base.VisitInterpolation(node)!;
			if (result.AlignmentClause is not null && result.AlignmentClause.Value is not LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression })
			{
				result = result.WithAlignmentClause(null);
			}
			return result;
		}
	}
}
