using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Filters;
using BenchmarkDotNet.Running;

namespace SharpDbg.Benchmarks.Reporting;

/// <summary>
/// What a result depends on, each part a hash: the benchmark's source (its class and what every benchmark shares),
/// the adapter's files and arguments, and the machine (the environment lines BenchmarkDotNet reports).
/// </summary>
public sealed record Fingerprint(string Benchmark, string Adapter, string Environment);

/// <summary>
/// <c>--only-changed</c>: sharpdbg always runs, and another adapter's result is reused from an earlier run when its fingerprint is unchanged
/// and it ran recently enough (<c>--max-age</c>, in days; 0, the default, is today). <c>--rerun</c> runs the adapters named regardless.
/// Earlier results are every <c>*-results.json</c> in the results directory, so a run that measured only some benchmarks still counts.
/// </summary>
public static class Reuse
{
	public static bool Enabled { get; set; }
	public static HashSet<string> Rerun { get; set; } = [];
	public static int MaxAgeDays { get; set; }

	public static string ResultsDirectory => RepoPaths.Join("artifacts", "benchmarks", "results");

	// What every benchmark's timing depends on besides its own class: the session and DAP client, the debuggee, and the job settings.
	// Not SharpDbg.DapProtocol, which the client serializes with: it changes with sharpdbg, and would rerun every adapter.
	private static readonly string[][] SharedSources =
	[
		["benchmarks", "SharpDbg.Benchmarks", "Benchmarks", "AdapterBenchmark.cs"],
		["benchmarks", "SharpDbg.Benchmarks", "DebugSession.cs"],
		["benchmarks", "SharpDbg.Benchmarks", "DebuggeeSource.cs"],
		["benchmarks", "SharpDbg.Benchmarks", "Program.cs"],
		["benchmarks", "BenchmarkDebuggee", "Program.cs"],
		["benchmarks", "BenchmarkDebuggee", "BenchmarkDebuggee.csproj"],
	];

	private static readonly ConcurrentDictionary<Type, string> BenchmarkHashes = new();
	private static readonly ConcurrentDictionary<string, string> AdapterHashes = new();
	private static readonly Lazy<string> EnvironmentHash = new(() => Hash(string.Join("\n", EnvironmentLines(HostEnvironmentInfo.GetCurrent()))));
	private static readonly Lazy<List<EarlierResult>> Earlier = new(LoadEarlier);
	private static readonly ConcurrentDictionary<(string Id, string Adapter), bool> Decisions = new();
	private static readonly ConcurrentDictionary<(string Id, string Adapter), ResultCell> ReusedCells = new();

	public static IFilter Filter { get; } = new SimpleFilter(ShouldRun);

	public static IReadOnlyList<string> EnvironmentLines(HostEnvironmentInfo info) =>
		info.ToFormattedString().Where(line => string.IsNullOrWhiteSpace(line) is false).Select(line => line.Trim()).ToList();

	public static string Id(Type type, string method) => $"{type.Name}.{method}";

	public static string AdapterOf(BenchmarkCase benchmarkCase) => benchmarkCase.Parameters["Adapter"]?.ToString() ?? "";

	public static Fingerprint FingerprintOf(Type benchmark, string adapter) =>
		new(BenchmarkHashes.GetOrAdd(benchmark, HashBenchmark), AdapterHashes.GetOrAdd(adapter, HashAdapter), EnvironmentHash.Value);

	/// <summary>The earlier result used instead of running this benchmark for this adapter, if any.</summary>
	public static ResultCell? ReusedCell(string id, string adapter) => ReusedCells.GetValueOrDefault((id, adapter));

	private static bool IsBuilt(string adapter) => AdapterRegistry.Get(adapter).Path == AdapterRegistry.BuiltSharpDbg.Path;

	private static bool ShouldRun(BenchmarkCase benchmarkCase)
	{
		if (Enabled is false) return true;
		var descriptor = benchmarkCase.Descriptor;
		var adapter = AdapterOf(benchmarkCase);
		return Decisions.GetOrAdd((Id(descriptor.Type, descriptor.WorkloadMethod.Name), adapter), key => Decide(descriptor.Type, key.Id, adapter));
	}

	/// <summary>
	/// Why each adapter ran or reused each of these benchmarks. Printed once the results are in, as the filter also sees
	/// the benchmarks that <c>--filter</c> leaves out.
	/// </summary>
	public static IEnumerable<string> Explain(IEnumerable<string> ids) => ids.SelectMany(id => Reasons.Where(r => r.Key.Id == id).OrderBy(r => AdapterRegistry.All.ToList().FindIndex(a => a.Name == r.Key.Adapter)).Select(r => $"{id} [{r.Key.Adapter}] {r.Value}"));

	private static readonly ConcurrentDictionary<(string Id, string Adapter), string> Reasons = new();

	private static bool Decide(Type type, string id, string adapter)
	{
		bool Run(string why)
		{
			Reasons[(id, adapter)] = $"ran: {why}";
			return true;
		}

		if (IsBuilt(adapter)) return Run("built from this repository");
		if (Rerun.Contains(adapter)) return Run("--rerun");

		var fingerprint = FingerprintOf(type, adapter);
		var earlier = Earlier.Value.Where(e => e.Id == id && e.Adapter == adapter).OrderByDescending(e => e.Cell.Ran).ToList();
		if (earlier.Count == 0) return Run("no earlier result");
		var match = earlier.FirstOrDefault(e => e.Cell.Fingerprint == fingerprint);
		if (match is null) return Run($"{Changes(earlier[0].Cell.Fingerprint!, fingerprint)} since {earlier[0].Cell.Ran:yyyy-MM-dd HH:mm}");
		var ran = match.Cell.Ran!.Value;
		if (ran.Date < DateTime.Today.AddDays(-MaxAgeDays)) return Run($"last unchanged result is from {ran:yyyy-MM-dd}, older than --max-age {MaxAgeDays}");

		ReusedCells[(id, adapter)] = match.Cell with { Reused = true };
		Reasons[(id, adapter)] = $"reused from {ran:yyyy-MM-dd HH:mm}";
		return false;
	}

	private static string Changes(Fingerprint was, Fingerprint now)
	{
		var changes = new List<string>();
		if (was.Benchmark != now.Benchmark) changes.Add("benchmark");
		if (was.Adapter != now.Adapter) changes.Add("adapter");
		if (was.Environment != now.Environment) changes.Add("environment");
		return string.Join(", ", changes) + " changed";
	}

	private sealed record EarlierResult(string Id, string Adapter, ResultCell Cell);

	private static List<EarlierResult> LoadEarlier()
	{
		var results = new List<EarlierResult>();
		if (Directory.Exists(ResultsDirectory) is false) return results;
		foreach (var file in Directory.EnumerateFiles(ResultsDirectory, "*-results.json"))
		{
			ResultsModel? model;
			try
			{
				model = JsonSerializer.Deserialize<ResultsModel>(File.ReadAllText(file), ResultsExporter.JsonOptions);
			}
			catch (JsonException)
			{
				continue;
			}
			// Results from before fingerprints were recorded have no Id, Ran or Fingerprint, and are never reused
			foreach (var row in model?.Rows ?? [])
			{
				if (row.Id is null) continue;
				foreach (var (adapter, cell) in row.Cells)
				{
					if (cell.Ran is not null && cell.Fingerprint is not null) results.Add(new EarlierResult(row.Id, adapter, cell with { Reused = false }));
				}
			}
		}
		return results;
	}

	/// <summary>The benchmark's class, by the convention that it is in a file of its name, and the sources every benchmark shares.</summary>
	private static string HashBenchmark(Type type)
	{
		var directory = RepoPaths.Join("benchmarks", "SharpDbg.Benchmarks", "Benchmarks");
		var own = Path.Join(directory, type.Name + ".cs");
		// Without the convention, any benchmark changing counts as this one changing
		List<string> files = File.Exists(own) ? [own] : Directory.GetFiles(directory, "*.cs").Order(StringComparer.Ordinal).ToList();
		var text = new StringBuilder();
		foreach (var file in files.Concat(SharedSources.Select(RepoPaths.Join)))
		{
			// Line endings depend on how the repository was checked out
			text.Append(Path.GetRelativePath(RepoPaths.Root, file).Replace('\\', '/')).Append('\n');
			text.Append(File.Exists(file) ? File.ReadAllText(file).ReplaceLineEndings("\n") : "(missing)").Append('\n');
		}
		return Hash(text.ToString());
	}

	/// <summary>
	/// Every file beside the executable and under the adapter's <c>fingerprint</c> paths, by name, size and modified time (hashing the contents of
	/// a ReSharper install would take seconds each run), plus the arguments and configured version.
	/// The absolute paths are left out, so moving an adapter's folder keeps its results.
	/// </summary>
	private static string HashAdapter(string name)
	{
		var adapter = AdapterRegistry.Get(name);
		var text = new StringBuilder();
		text.Append("exe ").Append(Path.GetFileName(adapter.Path)).Append('\n');
		text.Append("args ").Append(string.Join(' ', adapter.Args)).Append('\n');
		text.Append("version ").Append(adapter.Version).Append('\n');
		var roots = new List<string> { Path.GetDirectoryName(Path.GetFullPath(adapter.Path))! };
		roots.AddRange(adapter.Fingerprint ?? []);
		for (var i = 0; i < roots.Count; i++)
		{
			text.Append("root ").Append(i).Append('\n');
			var root = roots[i];
			var files = Directory.Exists(root) ? Directory.GetFiles(root, "*", SearchOption.AllDirectories) : File.Exists(root) ? [root] : Array.Empty<string>();
			foreach (var file in files.Order(StringComparer.Ordinal))
			{
				var info = new FileInfo(file);
				var relative = Directory.Exists(root) ? Path.GetRelativePath(root, file) : Path.GetFileName(file);
				text.Append(relative.Replace('\\', '/')).Append(' ').Append(info.Length).Append(' ').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
			}
		}
		return Hash(text.ToString());
	}

	private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
}
