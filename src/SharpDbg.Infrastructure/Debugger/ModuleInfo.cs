using ClrDebug;

namespace SharpDbg.Infrastructure.Debugger;

/// <summary>
/// Tracks information about a loaded module including its symbol reader
/// </summary>
public class ModuleInfo : IDisposable
{
	/// <summary>
	/// The ICorDebugModule for this module
	/// </summary>
	public CorDebugModule Module { get; }

	/// <summary>
	/// Full path to the module on disk (if available)
	/// </summary>
	public string ModulePath { get; }

	/// <summary>
	/// Module name (typically the filename without path)
	/// </summary>
	public string ModuleName { get; }

	/// <summary>
	/// Symbol reader for this module (null if no PDB available)
	/// </summary>
	public SymbolReader? SymbolReader { get; }

	/// <summary>
	/// Base address of the module in memory
	/// </summary>
	public long BaseAddress { get; }

	public ModuleInfo(CorDebugModule module, string modulePath, SymbolReader? symbolReader)
	{
		Module = module;
		ModulePath = modulePath;
		ModuleName = Path.GetFileName(modulePath);
		SymbolReader = symbolReader;
		BaseAddress = (long)module.BaseAddress;
	}

	/// <summary>
	/// Check if this module contains the given source file
	/// </summary>
	public bool ContainsSourceFile(string sourceFilePath)
	{
		if (SymbolReader == null)
			return false;

		var normalizedPath = sourceFilePath.Replace('\\', '/');
		var fileName = Path.GetFileName(normalizedPath);

		foreach (var docPath in SymbolReader.GetSourceFiles())
		{
			var normalizedDocPath = docPath.Replace('\\', '/');

			// Try exact match
			if (string.Equals(normalizedPath, normalizedDocPath, StringComparison.OrdinalIgnoreCase))
				return true;

			// Try filename match
			var docFileName = Path.GetFileName(normalizedDocPath);
			if (string.Equals(fileName, docFileName, StringComparison.OrdinalIgnoreCase))
				return true;
		}

		return false;
	}

	public void Dispose()
	{
		SymbolReader?.Dispose();
	}
}
