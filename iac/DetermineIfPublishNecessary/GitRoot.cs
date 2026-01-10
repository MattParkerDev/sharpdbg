namespace DetermineIfPublishNecessary;

public static class GitRoot
{
	public static string GetGitRootPath()
	{
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

		return gitRoot;
	}
}
