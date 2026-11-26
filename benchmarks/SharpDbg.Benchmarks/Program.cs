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

// Our own option, the rest go to BenchmarkDotNet: --adapters sharpdbg,netcoredbg
var benchmarkArgs = new List<string>();
for (var i = 0; i < args.Length; i++)
{
	if (args[i] is "--adapters" && i + 1 < args.Length) AdapterRegistry.Filter = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
	else benchmarkArgs.Add(args[i]);
}

DebuggeeSource.Verify();
AdapterRegistry.Validate();

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
	.AddColumn(StatisticColumn.Median, StatisticColumn.P95, StatisticColumn.Min, StatisticColumn.Max)
	.HideColumns(Column.Job, Column.Toolchain, Column.RunStrategy, Column.WarmupCount, Column.IterationCount, Column.InvocationCount, Column.UnrollFactor, Column.Error)
	.WithSummaryStyle(SummaryStyle.Default.WithTimeUnit(TimeUnit.Millisecond))
	.WithArtifactsPath(RepoPaths.Join("artifacts", "benchmarks"))
	.WithOrderer(new DefaultOrderer(SummaryOrderPolicy.Declared, MethodOrderPolicy.Declared))
	.WithOptions(ConfigOptions.JoinSummary);

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(benchmarkArgs.ToArray(), config);
