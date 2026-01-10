using CliWrap;
using CliWrap.Buffered;
using InterpolatedParsing;
using PublishToNuget;

Console.WriteLine("Building NuGet package");
const string nugetPackageId = "SharpDbg";

var gitRootDirectory = GitRoot.GetGitRootPath();

var result = await Cli.Wrap("dotnet")
	.WithArguments("build -c Release")
	.WithWorkingDirectory(Path.Combine(gitRootDirectory, "src", $"{nugetPackageId}.InMemory"))
	.ExecuteBufferedAsync();

Console.WriteLine(result.StandardOutput);
Console.WriteLine(result.StandardError);

var publishFolderPath = Path.Combine(gitRootDirectory, "artifacts", "package", "release");
var folderInfo = new DirectoryInfo(publishFolderPath);
if (folderInfo.Exists is false) throw new DirectoryNotFoundException(publishFolderPath);

var packageFiles = folderInfo.EnumerateFiles("*.nupkg").ToList();
var packageFile = packageFiles.Single();
var packageVersionString = "";
InterpolatedParser.Parse(packageFile.Name, $"SharpDbg.{packageVersionString}.nupkg");
ArgumentException.ThrowIfNullOrWhiteSpace(packageVersionString);

Console.WriteLine("Package built and located");
Console.WriteLine("Publishing to Nuget");

var nugetToken = Environment.GetEnvironmentVariable("NUGET_AUTH_TOKEN");
ArgumentException.ThrowIfNullOrWhiteSpace(nugetToken, nameof(nugetToken));

var publishResult = await Cli.Wrap("dotnet")
	.WithArguments($"nuget push {packageFile.FullName} --api-key {nugetToken} --source https://api.nuget.org/v3/index.json")
	.WithValidation(CommandResultValidation.None)
	.ExecuteBufferedAsync();

nugetToken = null;

Console.WriteLine(publishResult.StandardOutput);
Console.WriteLine(publishResult.StandardError);

if (publishResult.ExitCode != 0)
{
	throw new Exception("Failed to publish package to Nuget");
}

var githubStepSummaryPath = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY")!;
var fileInfo = new FileInfo(githubStepSummaryPath);
if (fileInfo.Exists is false) throw new FileNotFoundException(githubStepSummaryPath);

var textToWrite = $"""
	[{nugetPackageId} {packageVersionString}](https://www.nuget.org/packages/{nugetPackageId}/{packageVersionString}) published
	""";

await File.WriteAllTextAsync(fileInfo.FullName, textToWrite);
