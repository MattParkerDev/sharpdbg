# Benchmarks

Speed benchmarks for sharpdbg and other .NET debuggers, measured through the Debug Adapter Protocol (DAP), the one interface they all share.
[BenchmarkDotNet](https://benchmarkdotnet.org) runs them with the Monitoring strategy: each iteration is a single, separately timed run.

- `SharpDbg.Benchmarks` - the benchmarks, and a small synchronous DAP client that drives each adapter over stdio.
- `Report` - turns a run's results into markdown and HTML reports.
- `BenchmarkDebuggee` - the program being debugged. The benchmarks use fixed lines in its `Program.cs` (listed in `SharpDbg.Benchmarks/DebuggeeSource.cs`), and check the text of each line before running, so if `Program.cs` is edited, update those line numbers.

## What is measured

Every benchmark also checks where the debugger stopped, so a debugger that stops in the wrong place fails instead of posting a time.

| Benchmark | Timed from → to |
|---|---|
| `LaunchToFirstBreakpoint` | starting the adapter process → stopped at a breakpoint on the debuggee's first line, top frame read |
| `StepOver` / `StepIn` / `StepOut` | the step request → the new stop's top frame read (when an IDE can move its cursor) |
| `ContinueToBreakpoint` | continue → stopped again at the same breakpoint, once round a loop |
| `ConditionalBreakpoint` | continue → stopped at a breakpoint whose condition is evaluated 1000 times on the way |
| `SetBreakpointOnEveryLine` | one `setBreakpoints` request for every line of `Program.cs` |
| `StackTrace` | all 23 frames of the stopped thread |
| `Locals` | `scopes` and the first scope's `variables` |
| `ExpandObject` / `ExpandList` | expanding an object with 26 members / a `List<int>` of 1000 |
| `Evaluate` | five watch expressions |

Apart from `LaunchToFirstBreakpoint`, each benchmark uses one debug session. Each iteration starts from a new stop, so nothing an adapter caches for a stop carries over to the next iteration.

## Running

Build and run in Release. This also builds sharpdbg in Release, and the debuggee in Debug:

```powershell
dotnet run -c Release --project benchmarks/SharpDbg.Benchmarks -- --filter '*'
```

- `--adapters sharpdbg,netcoredbg` - run only these adapters.
- `--filter '*Step*'` - run only these benchmarks. Any other [BenchmarkDotNet option](https://benchmarkdotnet.org/articles/guides/console-args.html) works too, e.g. `--iterationCount 5 --warmupCount 1` for a quicker run.

## Results

A run writes its results to `artifacts/benchmarks/results` as data only: `<run>-results.json`, also copied to `latest-results.json`. It holds every benchmark's median and spread for each adapter, why each failure failed, and the adapter versions and commit. Alongside are BenchmarkDotNet's own `*-report.csv` and `*-report-full.json` (every measurement).

Turn results into reports with `Report/render.cs`, a file-based app. It needs no benchmark run, so the reports can be changed and re-rendered at any time:

```powershell
dotnet run benchmarks/Report/render.cs
```

That renders the latest run. To render another, pass its results (`dotnet run benchmarks/Report/render.cs -- <results.json>`). `--baseline <adapter>` picks the adapter the others are compared with (sharpdbg by default), and `--out <directory>` where the reports go (beside the results by default). It writes:

- `latest-report.html` - open it in a browser. An overview heatmap (a row per benchmark, a column per adapter, darker the more times slower than the fastest), a bar chart per benchmark (the median, with a whisker over the middle half of the runs, linear or log scale), the reason for each failure, and a table of every statistic. Hover over a cell or bar for its numbers. It is `Report/template.html` with the results embedded: a single file with no external dependencies.
- `latest-summary.md` - the same medians as a markdown table, each relative to the baseline, with failures as footnotes. Paste it into an issue or pull request.

The description under each benchmark's name comes from its `[Measures]` attribute.

## Adapters

Without configuration, only the sharpdbg built from this repository is benchmarked. To compare it with other debuggers, copy `SharpDbg.Benchmarks/adapters.example.json` to `adapters.json` (git ignored) next to it, and give each adapter a name, a path, and its arguments.

```json
{
	"adapters": [
		{ "name": "sharpdbg", "path": "built" },
		{ "name": "netcoredbg", "path": "C:/tools/netcoredbg/netcoredbg.exe", "args": ["--interpreter=vscode"] }
	]
}
```

`"path": "built"` is the sharpdbg in `artifacts/bin/SharpDbg.Cli/release`. An optional `"version"` is shown in the reports; without it, the executable's product version is used. Any adapter that speaks DAP over stdio and accepts a `coreclr`-style launch request (`program`, `args`, `cwd`, `justMyCode`) can be added: netcoredbg, dncdbg and clrdbg work as they are.

## Getting stable numbers

- Close other heavy work: a debugger's timings depend heavily on CPU scheduling.
- On Windows, Defender scanning newly built files slows the first launches. Exclude `artifacts` or discard the first run.
- Debuggers' defaults differ (symbol loading, Just My Code, how collections are shown). The launch request asks for Just My Code everywhere, and the rest is left at each adapter's defaults.
