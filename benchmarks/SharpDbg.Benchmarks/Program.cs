using BenchmarkDotNet.Analysers;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Exporters.Csv;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Order;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using Perfolizer.Horology;
using SharpDbg.Benchmarks;
using SharpDbg.Benchmarks.Reporting;

FailureLog.CaptureConsole();

// Our own options, the rest go to BenchmarkDotNet: --adapters sharpdbg,netcoredbg, --only-changed, --rerun netcoredbg, --max-age 3
static HashSet<string> Names(string list) => list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
var benchmarkArgs = new List<string>();
for (var i = 0; i < args.Length; i++)
{
	switch (args[i])
	{
		case "--adapters" when i + 1 < args.Length: AdapterRegistry.Filter = Names(args[++i]); break;
		case "--only-changed": Reuse.Enabled = true; break;
		case "--rerun" when i + 1 < args.Length: Reuse.Rerun = Names(args[++i]); Reuse.Enabled = true; break;
		case "--max-age" when i + 1 < args.Length && int.TryParse(args[i + 1], out var days) && days >= 0: Reuse.MaxAgeDays = days; i++; break;
		case "--max-age": throw new ArgumentException("--max-age takes a number of days, 0 for today");
		default: benchmarkArgs.Add(args[i]); break;
	}
}

DebuggeeSource.Verify();
AdapterRegistry.Validate();
var unknownRerun = Reuse.Rerun.Except(AdapterRegistry.All.Select(a => a.Name)).ToList();
if (unknownRerun.Count > 0) throw new ArgumentException($"Unknown adapter(s) '{string.Join("', '", unknownRerun)}' in --rerun. Configured: {string.Join(", ", AdapterRegistry.All.Select(a => a.Name))}");
// The results' rows come from what ran, so a benchmark every adapter reused would be left out
if (Reuse.Enabled && AdapterRegistry.SelectedNames.All(name => AdapterRegistry.Get(name).Path != AdapterRegistry.BuiltSharpDbg.Path))
	Console.WriteLine("// Warning: --only-changed without sharpdbg leaves out of the results each benchmark whose results were all reused");

// The time is spent in the adapter and debuggee processes, so the benchmarks run in this process (no generated project to build),
// and each iteration is a single, separately timed run of something that takes milliseconds to seconds
var job = Job.Default
	.WithToolchain(InProcessEmitToolchain.Instance)
	.WithStrategy(RunStrategy.Monitoring)
	.WithWarmupCount(2)
	.WithIterationCount(15)
	.WithInvocationCount(1)
	.WithUnrollFactor(1)
	.WithId("Monitoring");

var defaults = DefaultConfig.Instance;
var config = ManualConfig.CreateEmpty()
	.AddLogger(defaults.GetLoggers().ToArray())
	// Our results JSON (for benchmarks/Report/render.cs), plus BenchmarkDotNet's own CSV, and JSON with every measurement for comparing runs later
	.AddExporter(ResultsExporter.Instance, CsvExporter.Default, JsonExporter.Full)
	.AddColumnProvider(defaults.GetColumnProviders().ToArray())
	.AddValidator(defaults.GetValidators().ToArray())
	// A step taking a millisecond is the result, not a measurement too short to trust
	.AddAnalyser(defaults.GetAnalysers().Where(a => a is not MinIterationTimeAnalyser).ToArray())
	.AddJob(job.AsDefault())
	// --only-changed: skips each adapter's benchmarks whose earlier results are reused
	.AddFilter(Reuse.Filter)
	.AddColumn(StatisticColumn.Median, StatisticColumn.P95, StatisticColumn.Min, StatisticColumn.Max)
	.HideColumns(Column.Job, Column.Toolchain, Column.RunStrategy, Column.WarmupCount, Column.IterationCount, Column.InvocationCount, Column.UnrollFactor, Column.Error)
	.WithSummaryStyle(SummaryStyle.Default.WithTimeUnit(TimeUnit.Millisecond))
	.WithArtifactsPath(RepoPaths.Join("artifacts", "benchmarks"))
	.WithOrderer(new DefaultOrderer(SummaryOrderPolicy.Declared, MethodOrderPolicy.Declared))
	.WithOptions(ConfigOptions.JoinSummary);

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(benchmarkArgs.ToArray(), config);
