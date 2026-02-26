using System.Reflection.PortableExecutable;
using ClrDebug;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.DebugInfo;
using ICSharpCode.Decompiler.Metadata;
using SharpDbg.Infrastructure.Debugger.Decompilation;

namespace SharpDbg.Infrastructure.Debugger;

public readonly record struct SourceInfo(string FilePath, int StartLine, int StartColumn, DecompiledSourceInfo? DecompilationReproductionInfo);
public class DecompiledSourceInfo
{
	public required string TypeFullName { get; init; }
	public required AssemblyPathAndMvid Assembly { get; init; }
	public required string CallingUserCodeAssemblyPath { get; init; }
}
public record struct AssemblyPathAndMvid(string AssemblyPath, Guid Mvid);
public partial class ManagedDebugger
{
	/// This appears to be 1 based, ie requires no adjustment when returned to the user
	private SourceInfo? GetSourceInfoAtFrame(CorDebugFrame frame)
	{
		if (frame is not CorDebugILFrame ilFrame)
			throw new InvalidOperationException("Active frame is not an IL frame");
		var function = ilFrame.Function;
		var module = _modules[function.Module.BaseAddress];
		if (module.SymbolReader is null)
		{
			if (module.IsUserCode) throw new InvalidOperationException("The module we are decompiling is user code - this should never happen, we should only be decompiling non user code modules");
			// No PDB on disk — generate one via decompilation and update the module entry
			var result = GetCachedOrGeneratePdb(module);
			if (result is not null)
			{
				module.SymbolReader = result;
				module.SymbolReaderFromDecompiled = true;
			}
		}

		if (module.SymbolReader is not null)
		{
			var ilOffset = ilFrame.IP.pnOffset;
			var methodToken = function.Token;
			var sourceInfo = module.SymbolReader.GetSourceLocationForOffset(methodToken, ilOffset);
			if (sourceInfo != null)
			{
				DecompiledSourceInfo? decompiledSourceInfo = null;
				if (module.SymbolReaderFromDecompiled)
				{
					var metadataImport = module.Module.GetMetaDataInterface().MetaDataImport;
					var mvid = metadataImport.ScopeProps.pmvid;
					var containingTypeDef = metadataImport.GetMethodProps(methodToken).pClass;
					var typeProps = metadataImport.GetTypeDefProps(containingTypeDef);
					var typeName = typeProps.szTypeDef;

					string? callingUserCodeAssemblyPath = null;
					var caller = frame.Caller;
					while (callingUserCodeAssemblyPath is null)
					{
						if (caller is null) break;

						if (caller is CorDebugILFrame callerIlFrame)
						{
							var callerFunction = callerIlFrame.Function;
							var callerModule = _modules[callerFunction.Module.BaseAddress];
							if (callerModule.IsUserCode)
							{
								callingUserCodeAssemblyPath = callerModule.ModulePath;
								break;
							}
						}

						caller = caller.Caller;
					}

					decompiledSourceInfo = new DecompiledSourceInfo
					{
						TypeFullName = typeName,
						Assembly = new AssemblyPathAndMvid(module.ModulePath, mvid),
						CallingUserCodeAssemblyPath = callingUserCodeAssemblyPath ?? throw new InvalidOperationException("Could not find a user code caller in the call stack")
					};
				}

				return new SourceInfo(sourceInfo.Value.sourceFilePath, sourceInfo.Value.startLine, sourceInfo.Value.startColumn, decompiledSourceInfo);
			}
		}

		return null;
	}

	private SymbolReader? GetCachedOrGeneratePdb(ModuleInfo moduleInfo)
	{
		var sharpIdeSymbolCachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "SharpIdeSymbolCache");
		var metadataImport = moduleInfo.Module.GetMetaDataInterface().MetaDataImport;
		var mvid = metadataImport.ScopeProps.pmvid;
		var assemblyName = Path.GetFileNameWithoutExtension(moduleInfo.ModuleName);
		var pdbPath = Path.Combine(sharpIdeSymbolCachePath, assemblyName, mvid.ToString(), $"{assemblyName}.decompiled.pdb");
		if (File.Exists(pdbPath))
		{
			var symbolReader = SymbolReader.TryLoadWithPdbPath(moduleInfo.ModulePath, pdbPath);
			if (symbolReader is null)
			{
				_logger?.Invoke($"GetCachedOrGeneratePdb: SymbolReader could not load cached PDB '{pdbPath}'");
				return null;
			}
			return symbolReader;
		}
		return GeneratePdb(moduleInfo, pdbPath);
	}


	private SymbolReader? GeneratePdb(ModuleInfo moduleInfo, string pdbPathToWriteTo)
	{
		var assemblyPath = moduleInfo.ModulePath;
		if (!File.Exists(assemblyPath)) return null;

		var allModulePaths = _modules.Values.Select(m => m.ModulePath).Where(p => !string.IsNullOrEmpty(p)).ToList();
		var resolver = new DebuggingAssemblyResolver(allModulePaths);

		PEFile file;
		try
		{
			file = new PEFile(assemblyPath, PEStreamOptions.PrefetchEntireImage);
		}
		catch (Exception ex)
		{
			_logger?.Invoke($"GeneratePdb: failed to open PE file '{assemblyPath}': {ex.Message}");
			return null;
		}

		using (file)
		{
			var decompilerSettings = new DecompilerSettings();
			var decompiler = new CSharpDecompiler(file, resolver, decompilerSettings)
			{
				AstTransforms = {
					new TransformFieldAndConstructorInitializers(),
					new AddXmlDocumentationTransform(),
					new EscapeInvalidIdentifiers(),
					new FixNameCollisions(),
					new ReplaceMethodCallsWithOperators()
				}
			};

			_logger?.Invoke($"GeneratePdb: writing PDB to '{pdbPathToWriteTo}' for '{assemblyPath}'");
			try
			{
				var pdbDirectory = Path.GetDirectoryName(pdbPathToWriteTo)!;
				if (!Directory.Exists(pdbDirectory)) Directory.CreateDirectory(pdbDirectory);
				using var pdbStream = File.Create(pdbPathToWriteTo);
				// noLogo: true until https://github.com/icsharpcode/ILSpy/pull/3667 is merged
				PortablePdbWriter.WritePdb(file, decompiler, decompilerSettings, pdbStream, noLogo: true);
			}
			catch (Exception ex)
			{
				_logger?.Invoke($"GeneratePdb: exception writing PDB: {ex}");
				return null;
			}

			var symbolReader = SymbolReader.TryLoadWithPdbPath(assemblyPath, pdbPathToWriteTo);
			if (symbolReader is null)
			{
				_logger?.Invoke($"GeneratePdb: SymbolReader could not load generated PDB '{pdbPathToWriteTo}'");
				return null;
			}

			_logger?.Invoke($"GeneratePdb: successfully loaded generated PDB for '{Path.GetFileName(assemblyPath)}'");
			return symbolReader;
		}
	}
}
