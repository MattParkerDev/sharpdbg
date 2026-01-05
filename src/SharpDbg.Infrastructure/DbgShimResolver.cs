using System.Runtime.InteropServices;

namespace SharpDbg.Infrastructure;

// https://github.com/lordmilko/ClrDebug/blob/master/Samples/NetCore/DbgShimResolver.cs
internal static class DbgShimResolver
{
	private const string DbgShimWinName = "dbgshim.dll";
	private const string DbgShimLinuxName = "libdbgshim.so";
	private const string DbgShimMacName = "libdbgshim.dylib";

	private const string WindowsRuntimePrefix = "win";
	private const string LinuxRuntimePrefix = "linux";
	private const string MacRuntimePrefix = "osx";

	//Use AppContext.BaseDirectory to ensure compatibility with NativeAOT
	private static string root = AppContext.BaseDirectory;

	internal static string Resolve()
	{
		var result = ResolveInternal();

		if (result == null)
			throw new InvalidOperationException($"Failed to find a runtime containing dbgshim.dll under '{root}'");

		return result;
	}

	private static string ResolveInternal()
	{
		if (!TryGetPlatformInfo(out var runtimePrefix, out var fileName))
			return null;

		//Have we been compiled for a specific RID?
		var filePath = Path.Combine(root, fileName);

		/* If we are a .NET Framework executable compiled without a RuntimeIdentifier, MSBuild
		 * may have implicitly decided we are win7-x86, thus copying a 32-bit dbgshim.dll to the
		 * output directory. This can be resolved by either adding an MSBuild target that deletes
		 * this dbgshim.dll file, or by modifying this resolver to instead try the "runtimes" directory
		 * first, and then look for a file directly within the output directory last. We check the output
		 * directory directly first because a. it is more efficient and b. you don't want a rogue dbgshim.dll
		 * in your output directory when you're intending to use the "runtimes" directory instead */
		if (File.Exists(filePath))
			return filePath;

		//Was a runtimes directory copied to our output folder?
		var runtimesDirectory = Path.Combine(root, "runtimes");

		if (!Directory.Exists(runtimesDirectory))
			return null;

		//ToLower() is used here to protect against case sensitive operating systems.
		//RuntimeInformation.ProcessArchitecture is present since .NET Framework 4.7.1
		filePath = ResolveFromRID(
			runtimesDirectory,
			string.Join("-", runtimePrefix, RuntimeInformation.ProcessArchitecture.ToString().ToLower()),
			fileName
		);

		if (filePath != null)
			return filePath;

#if NET5_0_OR_GREATER
		/* Try the RID reported by the runtime last (as we might get values such as "win10-x64" when what we really want is "win-x64",
		 * which we'll be able to match using the strategy above). Items such as linux-musl-* won't be matched by the previous
		 * strategies, and hopefully should be matched here */
		var rid = RuntimeInformation.RuntimeIdentifier;

		return ResolveFromRID(runtimesDirectory, rid, fileName);
#else
			return null;
#endif
	}

	private static string ResolveFromRID(string runtimesDirectory, string rid, string fileName)
	{
		//Do we have a directory for our target runtime? (e.g. win-x64)
		var runtimeDirectory = Path.Combine(
			runtimesDirectory,
			rid
		);

		if (!Directory.Exists(runtimeDirectory))
			return null;

		//There should be a native directory inside
		var nativeDirectory = Path.Combine(runtimeDirectory, "native");

		string filePath;

		if (!Directory.Exists(nativeDirectory))
		{
			//Has the native directory been elided via a custom task that constructed
			//the runtimes directory?

			filePath = Path.Combine(runtimeDirectory, fileName);

			if (File.Exists(filePath))
				return filePath;

			return null;
		}

		filePath = Path.Combine(nativeDirectory, fileName);

		if (File.Exists(filePath))
			return filePath;

		return null;
	}

	private static bool TryGetPlatformInfo(out string runtimePrefix, out string fileName)
	{
		//RuntimeInformation.IsOSPlatform requires at least .NET Framework 4.7.1

		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			runtimePrefix = WindowsRuntimePrefix;
			fileName = DbgShimWinName;
			return true;
		}

		if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
		{
			runtimePrefix = LinuxRuntimePrefix;
			fileName = DbgShimLinuxName;
			return true;
		}

		if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
		{
			runtimePrefix = MacRuntimePrefix;
			fileName = DbgShimMacName;
			return true;
		}

		runtimePrefix = null;
		fileName = null;
		return false;
	}
}
