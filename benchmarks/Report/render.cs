// File-based apps default to native AOT, which rules out reflection-based JSON (serializing records and an anonymous type here)
#:property PublishAot=false

// Renders a benchmark run's results JSON (written by SharpDbg.Benchmarks) as a markdown table and a self-contained HTML report.
//
//   dotnet run benchmarks/Report/render.cs                      the latest run
//   dotnet run benchmarks/Report/render.cs -- <results.json>     a given run
//   options: --baseline <adapter>   the adapter others are compared with (default: sharpdbg, if it ran)
//            --out <directory>      where to write the reports (default: beside the results)
//
// The reports are <run>-summary.md and <run>-report.html, and for the latest run, latest-summary.md and latest-report.html.
// The HTML is template.html, beside this file, with the results embedded.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

string? resultsPath = null, baseline = null, outDirectory = null;
for (var i = 0; i < args.Length; i++)
{
	switch (args[i])
	{
		case "--baseline" when i + 1 < args.Length: baseline = args[++i]; break;
		case "--out" when i + 1 < args.Length: outDirectory = args[++i]; break;
		case var arg when arg.StartsWith("--", StringComparison.Ordinal): return Fail($"Unknown option {arg}");
		default: resultsPath = args[i]; break;
	}
}

var scriptDirectory = ScriptDirectory();
resultsPath ??= Path.Join(RepoRoot(scriptDirectory), "artifacts", "benchmarks", "results", "latest-results.json");
if (File.Exists(resultsPath) is false) return Fail($"No results at {resultsPath}. Run the benchmarks first: dotnet run -c Release --project benchmarks/SharpDbg.Benchmarks -- --filter '*'");

var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false };
var results = JsonSerializer.Deserialize<Results>(File.ReadAllText(resultsPath), jsonOptions) ?? throw new InvalidOperationException($"{resultsPath} is empty");
baseline ??= results.Adapters.Any(a => a.Name == "sharpdbg") ? "sharpdbg" : results.Adapters.FirstOrDefault()?.Name ?? "";
if (results.Adapters.All(a => a.Name != baseline)) return Fail($"No adapter '{baseline}' in the results. Adapters: {string.Join(", ", results.Adapters.Select(a => a.Name))}");

outDirectory ??= Path.GetDirectoryName(Path.GetFullPath(resultsPath))!;
Directory.CreateDirectory(outDirectory);
var fileName = Path.GetFileName(resultsPath);
var run = fileName.EndsWith("-results.json", StringComparison.Ordinal) ? fileName[..^"-results.json".Length] : Path.GetFileNameWithoutExtension(fileName);

var markdown = Markdown.Render(results, baseline);
var html = File.ReadAllText(Path.Join(scriptDirectory, "template.html"))
	// The default encoder escapes '<', so nothing in the results can end the script element they are embedded in
	.Replace("/*DATA*/", JsonSerializer.Serialize(new { results.Title, Date = results.Date.ToString("yyyy-MM-dd HH:mm"), results.Environment, results.Commit, Baseline = baseline, results.Adapters, results.Rows }, jsonOptions));

foreach (var (name, content) in (ReadOnlySpan<(string, string)>)[($"{run}-summary.md", markdown), ($"{run}-report.html", html)])
{
	var path = Path.Join(outDirectory, name);
	File.WriteAllText(path, content);
	Console.WriteLine($"Wrote {path}");
}
Console.WriteLine();
Console.WriteLine(markdown);
return 0;

static int Fail(string message)
{
	Console.Error.WriteLine(message);
	return 1;
}

static string ScriptDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

static string RepoRoot(string from)
{
	for (var directory = from; directory is not null; directory = Path.GetDirectoryName(directory))
	{
		if (Directory.Exists(Path.Join(directory, ".git")) || File.Exists(Path.Join(directory, ".git"))) return directory;
	}
	throw new InvalidOperationException("Could not find the repository root");
}

/// <summary>A row per benchmark and a column per adapter, holding medians: the table to paste into a README or pull request.</summary>
static class Markdown
{
	public static string Render(Results results, string baseline)
	{
		var failures = new List<string>();
		var md = new StringBuilder();
		md.AppendLine("# Debugger benchmarks");
		md.AppendLine();
		md.AppendLine($"Median time of each operation, lower is better. **Bold** is the fastest, and ×N is the time relative to {baseline}.");
		md.AppendLine();

		md.Append("| Benchmark |");
		foreach (var adapter in results.Adapters) md.Append($" {adapter.Name}{(adapter.Version is null ? "" : $"<br><sub>{adapter.Version}</sub>")} |");
		md.AppendLine();
		md.Append("|---|");
		foreach (var _ in results.Adapters) md.Append("--:|");
		md.AppendLine();

		foreach (var group in results.Rows.GroupBy(r => r.Group))
		{
			md.Append($"| **{group.Key}** |");
			foreach (var _ in results.Adapters) md.Append(" |");
			md.AppendLine();
			foreach (var row in group)
			{
				var fastest = row.Cells.Values.Select(c => c.Median).Min();
				var baselineMedian = row.Cells.GetValueOrDefault(baseline)?.Median;
				md.Append($"| {row.Name} |");
				foreach (var adapter in results.Adapters)
				{
					md.Append(' ');
					var cell = row.Cells.GetValueOrDefault(adapter.Name);
					if (cell?.Median is { } median)
					{
						var time = Format.Duration(median);
						md.Append(median == fastest ? $"**{time}**" : time);
						if (adapter.Name != baseline && baselineMedian is { } b) md.Append($" ×{Format.Ratio(median / b)}");
					}
					else if (cell is not null)
					{
						failures.Add($"{adapter.Name}, {row.Name}: {Escape(cell.Failure ?? "failed")}");
						md.Append($"✗ <sup>{failures.Count}</sup>");
					}
					else md.Append('–');
					md.Append(" |");
				}
				md.AppendLine();
			}
		}

		if (failures.Count > 0)
		{
			md.AppendLine();
			md.AppendLine("Failed:");
			md.AppendLine();
			for (var i = 0; i < failures.Count; i++) md.AppendLine($"{i + 1}. {failures[i]}");
		}

		var reused = Reused(results);
		if (reused.Count > 0)
		{
			md.AppendLine();
			md.AppendLine($"Reused from earlier runs, as the adapter, benchmark and machine are unchanged: {string.Join("; ", reused)}.");
		}

		md.AppendLine();
		var n = results.Rows.SelectMany(r => r.Cells.Values).Select(c => c.N).DefaultIfEmpty().Max();
		md.AppendLine($"{results.Date:yyyy-MM-dd HH:mm} · sharpdbg {results.Commit} · {n} measured iterations each");
		md.AppendLine();
		md.AppendLine("```");
		foreach (var line in results.Environment) md.AppendLine(line);
		md.AppendLine("```");
		return md.ToString();
	}

	/// <summary>Per adapter with reused results: "dncdbg, 12 of 15 from 2026-11-27 11:59".</summary>
	private static List<string> Reused(Results results) => results.Adapters
		.Select(adapter => (adapter.Name, Cells: results.Rows.Select(r => r.Cells.GetValueOrDefault(adapter.Name)).OfType<Cell>().ToList()))
		.Select(a => (a.Name, a.Cells.Count, Reused: a.Cells.Where(c => c.Reused).ToList()))
		.Where(a => a.Reused.Count > 0)
		.Select(a =>
		{
			var times = a.Reused.Select(c => c.Ran).OfType<DateTime>().Select(t => t.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Distinct().Order().ToList();
			var when = times.Count switch { 0 => "", 1 => $" from {times[0]}", _ => $" from {times[0]} to {times[^1]}" };
			return $"{a.Name}, {(a.Reused.Count == a.Count ? "all" : $"{a.Reused.Count} of {a.Count}")}{when}";
		})
		.ToList();

	// A failure is one line in a list item, and must not end the table or start formatting
	private static string Escape(string text) => string.Join(" ", text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
		.Replace("|", "\\|").Replace("<", "&lt;").Replace("*", "\\*").Replace("_", "\\_");
}

static class Format
{
	/// <summary>Three significant figures: 0.651 ms, 16.2 ms, 296 ms, 1.06 s.</summary>
	public static string Duration(double milliseconds) => milliseconds switch
	{
		>= 1000 => (milliseconds / 1000).ToString("0.00", CultureInfo.InvariantCulture) + " s",
		>= 100 => milliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms",
		>= 10 => milliseconds.ToString("0.0", CultureInfo.InvariantCulture) + " ms",
		>= 1 => milliseconds.ToString("0.00", CultureInfo.InvariantCulture) + " ms",
		_ => milliseconds.ToString("0.000", CultureInfo.InvariantCulture) + " ms",
	};

	public static string Ratio(double ratio) => ratio >= 10
		? ratio.ToString("0", CultureInfo.InvariantCulture)
		: ratio.ToString("0.0#", CultureInfo.InvariantCulture);
}

// The shape SharpDbg.Benchmarks writes (its Reporting/ResultsModel.cs). Times are in milliseconds; a null median is a failure.
record Results(string Title, DateTime Date, List<string> Environment, string Commit, List<Adapter> Adapters, List<Row> Rows);
record Adapter(string Name, string? Version);
record Row(string Group, string Name, string Measures, Dictionary<string, Cell> Cells);
// Ran and Reused are absent from results written before --only-changed; Reused is true for a result measured in an earlier run
record Cell(double? Median, double? Mean, double? Min, double? Q1, double? Q3, double? P95, double? Max, int N, string? Failure, DateTime? Ran, bool Reused);
