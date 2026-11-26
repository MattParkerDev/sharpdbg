using System.Text.Json;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Reports;

namespace SharpDbg.Benchmarks.Reporting;

/// <summary>
/// Writes the run's <see cref="ResultsModel"/> as JSON: the medians and spread BenchmarkDotNet measured, plus what its own export lacks
/// (why each failure failed, adapter versions, the commit). <c>benchmarks/Report/render.cs</c> turns it into markdown and HTML,
/// so the reports can be changed and re-rendered without running the benchmarks again.
/// </summary>
public sealed class ResultsExporter : IExporter
{
	public static readonly ResultsExporter Instance = new();

	public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

	public string Name => "SharpDbgResults";

	public void ExportToLog(Summary summary, ILogger logger) => logger.WriteLine(JsonSerializer.Serialize(ResultsModel.From(summary), JsonOptions));

	public IEnumerable<string> ExportToFiles(Summary summary, ILogger consoleLogger)
	{
		var json = JsonSerializer.Serialize(ResultsModel.From(summary), JsonOptions);
		Directory.CreateDirectory(summary.ResultsDirectoryPath);
		var run = Path.Join(summary.ResultsDirectoryPath, $"{summary.Title}-results.json");
		var latest = Path.Join(summary.ResultsDirectoryPath, "latest-results.json");
		File.WriteAllText(run, json);
		File.WriteAllText(latest, json);

		consoleLogger.WriteLine();
		consoleLogger.WriteLineInfo("Render the markdown and HTML reports with: dotnet run benchmarks/Report/render.cs");
		return [run, latest];
	}
}
