using System.Diagnostics;
using System.Reflection;
using BenchmarkDotNet.Reports;

namespace SharpDbg.Benchmarks.Reporting;

/// <summary>What a benchmark times, from → to, shown under its name in the reports.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class MeasuresAttribute(string description) : Attribute
{
	public string Description { get; } = description;
}

/// <summary>The group a benchmark class's rows are shown under, instead of its name without "Benchmarks".</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class GroupAttribute(string name) : Attribute
{
	public string Name { get; } = name;
}

/// <summary>
/// Times in milliseconds. A null median means the benchmark failed for that adapter.
/// <paramref name="Ran"/> and <paramref name="Fingerprint"/> say when it was measured and on what, for <see cref="Reuse"/>;
/// <paramref name="Reused"/> is true when it was measured in an earlier run.
/// </summary>
public sealed record ResultCell(double? Median, double? Mean, double? Min, double? Q1, double? Q3, double? P95, double? Max, int N, string? Failure,
	DateTime? Ran = null, Fingerprint? Fingerprint = null, bool Reused = false);

/// <param name="Id">The benchmark's class and method, e.g. <c>StepBenchmarks.StepOver</c>.</param>
public sealed record ResultRow(string? Id, string Group, string Name, string Measures, IReadOnlyDictionary<string, ResultCell> Cells);

public sealed record ResultAdapter(string Name, string? Version);

/// <summary>
/// A run's results as one table: a row per benchmark, a cell per adapter. Written as JSON by <see cref="ResultsExporter"/>,
/// and turned into reports by <c>benchmarks/Report/render.cs</c>, which has its own copy of these records.
/// </summary>
public sealed record ResultsModel(
	string Title,
	DateTime Date,
	IReadOnlyList<string> Environment,
	string Commit,
	IReadOnlyList<ResultAdapter> Adapters,
	IReadOnlyList<ResultRow> Rows)
{
	private const double NanosecondsPerMillisecond = 1_000_000;
	private static readonly List<string> GroupOrder = ["Launch", "Step", "Breakpoint", "Inspect"];

	public static ResultsModel From(Summary summary)
	{
		var ran = DateTime.Now;
		var rows = summary.Reports
			.GroupBy(r => (Type: r.BenchmarkCase.Descriptor.Type, Method: r.BenchmarkCase.Descriptor.WorkloadMethod))
			.Select(g =>
			{
				var id = Reuse.Id(g.Key.Type, g.Key.Method.Name);
				var cells = g.ToDictionary(r => AdapterOf(r.BenchmarkCase), r => Cell(r) with { Ran = ran, Fingerprint = Reuse.FingerprintOf(g.Key.Type, AdapterOf(r.BenchmarkCase)) });
				// With --only-changed, the adapters that did not run this benchmark carry over their earlier result
				foreach (var adapter in AdapterRegistry.SelectedNames)
				{
					if (cells.ContainsKey(adapter) is false && Reuse.ReusedCell(id, adapter) is { } reused) cells[adapter] = reused;
				}
				return new ResultRow(
					Id: id,
					Group: g.Key.Type.GetCustomAttribute<GroupAttribute>()?.Name ?? g.Key.Type.Name.Replace("Benchmarks", ""),
					Name: g.Key.Method.Name,
					Measures: g.Key.Method.GetCustomAttribute<MeasuresAttribute>()?.Description ?? "",
					Cells: cells);
			})
			// Stable sort: the order an IDE session goes through, then the order the methods are declared in
			.OrderBy(row => GroupOrder.IndexOf(row.Group) is var i and >= 0 ? i : GroupOrder.Count)
			.ToList();

		var adapterNames = rows.SelectMany(r => r.Cells.Keys).Distinct().ToList();
		// Keep the order of adapters.json, so each adapter keeps its column (and colour) from run to run
		var ordered = AdapterRegistry.All.Select(a => a.Name).Where(adapterNames.Contains).Concat(adapterNames.Except(AdapterRegistry.All.Select(a => a.Name))).ToList();

		return new ResultsModel(
			summary.Title,
			ran,
			Reuse.EnvironmentLines(summary.HostEnvironmentInfo),
			GitCommit(),
			ordered.Select(name => new ResultAdapter(name, AdapterVersion(name))).ToList(),
			rows);
	}

	private static string AdapterOf(BenchmarkDotNet.Running.BenchmarkCase benchmarkCase) => Reuse.AdapterOf(benchmarkCase);

	private static ResultCell Cell(BenchmarkReport report)
	{
		var statistics = report.ResultStatistics;
		if (report.Success is false || statistics is null) return new ResultCell(null, null, null, null, null, null, null, 0, FailureReason(report));
		return new ResultCell(
			statistics.Median / NanosecondsPerMillisecond,
			statistics.Mean / NanosecondsPerMillisecond,
			statistics.Min / NanosecondsPerMillisecond,
			statistics.Q1 / NanosecondsPerMillisecond,
			statistics.Q3 / NanosecondsPerMillisecond,
			statistics.Percentiles.P95 / NanosecondsPerMillisecond,
			statistics.Max / NanosecondsPerMillisecond,
			statistics.N,
			null);
	}

	private static string FailureReason(BenchmarkReport report)
	{
		var descriptor = report.BenchmarkCase.Descriptor;
		var adapter = AdapterOf(report.BenchmarkCase);
		var message = FailureLog.Get(descriptor.Type.Name, descriptor.WorkloadMethod.Name, adapter);
		if (message is null) return "Failed (see the console output)";
		if (message.StartsWith(adapter + ": ", StringComparison.Ordinal)) message = message[(adapter.Length + 2)..];
		return message.Replace(Path.GetDirectoryName(DebuggeeSource.ProgramPath) + Path.DirectorySeparatorChar, "").Trim();
	}

	private static string? AdapterVersion(string name)
	{
		var adapter = AdapterRegistry.All.FirstOrDefault(a => a.Name == name);
		if (adapter is null) return null;
		if (adapter.Version is not null) return adapter.Version;
		// Built from this repository, so its assembly version is just the default
		if (adapter.Path == AdapterRegistry.BuiltSharpDbg.Path) return $"build {RunGitOrNull("rev-parse --short HEAD") ?? "local"}";
		// An apphost carries no version, the assembly beside it does
		foreach (var file in (string[])[adapter.Path, Path.ChangeExtension(adapter.Path, ".dll")])
		{
			if (File.Exists(file) is false) continue;
			var version = FileVersionInfo.GetVersionInfo(file).ProductVersion;
			if (string.IsNullOrWhiteSpace(version) is false) return version.Split('+')[0];
		}
		return null;
	}

	private static string GitCommit()
	{
		try
		{
			var commit = RunGit("rev-parse --short HEAD");
			var dirty = RunGit("status --porcelain --untracked-files=no").Length > 0;
			return dirty ? $"{commit} (with uncommitted changes)" : commit;
		}
		catch
		{
			return "unknown";
		}
	}

	private static string? RunGitOrNull(string arguments)
	{
		try
		{
			return RunGit(arguments);
		}
		catch
		{
			return null;
		}
	}

	private static string RunGit(string arguments)
	{
		using var git = Process.Start(new ProcessStartInfo("git", arguments) { WorkingDirectory = RepoPaths.Root, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
		var output = git.StandardOutput.ReadToEnd().Trim();
		git.WaitForExit();
		return output;
	}
}
