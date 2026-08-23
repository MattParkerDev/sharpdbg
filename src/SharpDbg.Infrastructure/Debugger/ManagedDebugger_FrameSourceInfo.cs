using System.Reflection.PortableExecutable;
using ICorDebugSharp;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.DebugInfo;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using SharpDbg.Infrastructure.Debugger.Decompilation;

namespace SharpDbg.Infrastructure.Debugger;

public readonly record struct SourceInfo(string FilePath, int StartLine, int EndLine, int StartColumn, int EndColumn, DecompiledSourceInfo? DecompiledSourceInfo);
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
	private SourceInfo? GetSourceInfoAtFrame(ICorDebugFrame frame, bool decompileIfNeeded)
	{
		if (frame is not ICorDebugILFrame ilFrame)
			throw new InvalidOperationException("Active frame is not an IL frame");
		var function = ilFrame.Function;
		var module = _modules[function.Module.BaseAddress];
		if (module.MetadataReader.HasSymbols is false && decompileIfNeeded)
		{
			// No PDB on disk — generate one via decompilation and update the module entry
			if (GetCachedOrGeneratePdb(module))
			{
				module.SymbolsFromDecompiled = true;
			}
		}

		if (module.MetadataReader.HasSymbols)
		{
			var ilOffset = ilFrame.IP.pnOffset;
			var methodToken = function.Token;
			var sourceInfo = module.MetadataReader.GetSourceLocationForOffset(methodToken, ilOffset);
			if (sourceInfo is not null)
			{
				DecompiledSourceInfo? decompiledSourceInfo = null;
				if (module.SymbolsFromDecompiled)
				{
					var callingUserCodeAssemblyPath = FindCallingUserCodeAssemblyPath(frame.Caller);
					decompiledSourceInfo = CreateDecompiledSourceInfo(module, methodToken, callingUserCodeAssemblyPath);
				}

				return new SourceInfo(sourceInfo.Value.sourceFilePath, sourceInfo.Value.startLine, sourceInfo.Value.endLine, sourceInfo.Value.startColumn, sourceInfo.Value.endColumn, decompiledSourceInfo);
			}
		}

		return null;
	}

	/// Walks the physical caller chain looking for the closest frame from a user code assembly
	private string? FindCallingUserCodeAssemblyPath(ICorDebugFrame? startFrame)
	{
		for (var frame = startFrame; frame is not null; frame = frame.Caller)
		{
			if (frame is not ICorDebugILFrame ilFrame) continue;
			var callerModule = _modules[ilFrame.Function.Module.BaseAddress];
			if (callerModule.IsUserCode) return callerModule.ModulePath;
		}
		return null;
	}

	private static DecompiledSourceInfo? CreateDecompiledSourceInfo(ModuleInfo module, int methodToken, string? callingUserCodeAssemblyPath)
	{
		if (callingUserCodeAssemblyPath is null) return null;
		var metadataImport = module.Module.GetMetaDataInterface<IMetaDataImport>();
		var mvid = metadataImport.ScopeProps.pmvid;
		var containingTypeDef = metadataImport.GetMethodProps(methodToken).pClass;
		return new DecompiledSourceInfo
		{
			TypeFullName = GetFullMetadataTypeName(metadataImport, containingTypeDef),
			Assembly = new AssemblyPathAndMvid(module.ModulePath, mvid),
			CallingUserCodeAssemblyPath = callingUserCodeAssemblyPath
		};
	}

	private static string GetFullMetadataTypeName(IMetaDataImport metadataImport, mdTypeDef typeDef)
	{
		var typeProps = metadataImport.GetTypeDefProps(typeDef);
		if (typeProps.pdwTypeDefFlags.IsTdNested() is false) return typeProps.szTypeDef;

		var declaringType = metadataImport.GetNestedClassProps(typeDef);
		return $"{GetFullMetadataTypeName(metadataImport, declaringType)}+{typeProps.szTypeDef}";
	}

	private bool GetCachedOrGeneratePdb(ModuleInfo moduleInfo)
	{
		var sharpIdeSymbolCachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "SharpIdeSymbolCache");
		var metadataImport = moduleInfo.Module.GetMetaDataInterface<IMetaDataImport>();
		var mvid = metadataImport.ScopeProps.pmvid;
		var assemblyName = Path.GetFileNameWithoutExtension(moduleInfo.ModuleName);
		var pdbPath = Path.Combine(sharpIdeSymbolCachePath, assemblyName, mvid.ToString(), $"{assemblyName}.decompiled.pdb");
		if (File.Exists(pdbPath))
		{
			if (!moduleInfo.MetadataReader.TryLoadSymbols(pdbPath))
			{
				_logger?.Invoke($"GetCachedOrGeneratePdb: could not load cached PDB '{pdbPath}'");
				return false;
			}
			return true;
		}
		return GeneratePdb(moduleInfo, pdbPath);
	}


	private bool GeneratePdb(ModuleInfo moduleInfo, string pdbPathToWriteTo)
	{
		var assemblyPath = moduleInfo.ModulePath;
		if (!File.Exists(assemblyPath)) return false;

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
			return false;
		}

		using (file)
		{
			var decompilerSettings = new DecompilerSettings();
			var decompilerTypeSystem = new DecompilerTypeSystem(file, resolver, decompilerSettings);

			_logger?.Invoke($"GeneratePdb: writing PDB to '{pdbPathToWriteTo}' for '{assemblyPath}'");
			try
			{
				var pdbDirectory = Path.GetDirectoryName(pdbPathToWriteTo)!;
				if (!Directory.Exists(pdbDirectory)) Directory.CreateDirectory(pdbDirectory);
				using var pdbStream = File.Create(pdbPathToWriteTo);
				var portablePdbWriter2 = new PortablePdbWriter2 { NoLogo = true };
				portablePdbWriter2.WritePdb(file, decompilerTypeSystem, decompilerSettings, pdbStream);
			}
			catch (Exception ex)
			{
				_logger?.Invoke($"GeneratePdb: exception writing PDB: {ex}");
				File.Delete(pdbPathToWriteTo);
				return false;
			}

			if (!moduleInfo.MetadataReader.TryLoadSymbols(pdbPathToWriteTo))
			{
				_logger?.Invoke($"GeneratePdb: could not load generated PDB '{pdbPathToWriteTo}'");
				return false;
			}

			_logger?.Invoke($"GeneratePdb: successfully loaded generated PDB for '{Path.GetFileName(assemblyPath)}'");
			return true;
		}
	}
}
