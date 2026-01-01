namespace SharpDbg.Cli.Tests.Helpers;

public static class GitRoot
{
	private static string? _gitRoot;
	public static string GetGitRootPath()
	{
		if (_gitRoot is not null) return _gitRoot;
		var currentDirectory = Directory.GetCurrentDirectory();
		var gitRoot = currentDirectory;
		while (!Directory.Exists(Path.Combine(gitRoot, ".git")))
		{
			gitRoot = Path.GetDirectoryName(gitRoot); // parent directory
			if (string.IsNullOrWhiteSpace(gitRoot))
			{
				throw new Exception("Could not find git root");
			}
		}

		_gitRoot = gitRoot;
		return _gitRoot;
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
