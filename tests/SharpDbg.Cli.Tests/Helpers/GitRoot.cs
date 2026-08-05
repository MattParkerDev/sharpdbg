namespace SharpDbg.Cli.Tests.Helpers;

public static class GitRoot
{
	private static string? _gitRoot;
	public static string GetGitRootPath()
	{
		if (_gitRoot is not null) return _gitRoot;
		var currentDirectory = Directory.GetCurrentDirectory();
		while (FolderOrFileExists(currentDirectory) is false)
		{
			currentDirectory = Path.GetDirectoryName(currentDirectory); // parent directory
			if (string.IsNullOrWhiteSpace(currentDirectory))
			{
				throw new Exception("Could not find git root");
			}
		}

		_gitRoot = currentDirectory;
		return _gitRoot;
	}
	// worktrees and submodules use a .git file
	private static bool FolderOrFileExists(string directory)
	{
		var dotGitPath = Path.Combine(directory, ".git");
		return Directory.Exists(dotGitPath) || File.Exists(dotGitPath);
	}
}

public static class PathExtensions
{
	extension(Path)
	{
		public static string JoinFromGitRoot(params ReadOnlySpan<string?> paths)
		{
			return Path.Join([GitRoot.GetGitRootPath(), ..paths]);
		}
	}
}
