using DetermineIfPublishNecessary;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Locator;
using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

MSBuildLocator.RegisterDefaults();
await Run(); // https://learn.microsoft.com/en-us/visualstudio/msbuild/find-and-use-msbuild-versions?view=vs-2022#register-instance-before-calling-msbuild

return;

async Task Run()
{
	const string nugetPackageId = "SharpDbg";
	var gitRootDirectory = GitRoot.GetGitRootPath();
	var packageProjectFilePath = Path.Combine(gitRootDirectory, "src", $"{nugetPackageId}.InMemory" , $"{nugetPackageId}.InMemory.csproj");

	var fileInfo = new FileInfo(packageProjectFilePath);
	if (fileInfo.Exists is false)
	{
		throw new FileNotFoundException(packageProjectFilePath);
	}

	Console.WriteLine($"Found project file at: {packageProjectFilePath}");

	var project = Project.FromFile(packageProjectFilePath, new ProjectOptions());
	var packageVersionString = project.GetProperty("PackageVersion").EvaluatedValue;
	var packageVersion = NuGetVersion.Parse(packageVersionString);

	var cache = new SourceCacheContext();
	var repositories = Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");
	var logger = NullLogger.Instance;

	var resource = await repositories.GetResourceAsync<FindPackageByIdResource>();
	var packageAndSpecificVersionExistsOnNuget = await resource.DoesPackageExistAsync(nugetPackageId, packageVersion, cache, logger, CancellationToken.None);
	if (packageAndSpecificVersionExistsOnNuget)
	{
		Console.WriteLine($"Package {nugetPackageId} with version {packageVersion} exists on Nuget");
	}
	else
	{
		Console.WriteLine($"Package {nugetPackageId} with version {packageVersion} does not exist on Nuget");
		var outputFile = Environment.GetEnvironmentVariable("GITHUB_OUTPUT");
		await File.AppendAllTextAsync(outputFile!, "publish-necessary=true");
	}
}
