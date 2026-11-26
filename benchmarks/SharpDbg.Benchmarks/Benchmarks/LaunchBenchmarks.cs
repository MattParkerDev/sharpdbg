using BenchmarkDotNet.Attributes;
using SharpDbg.DapProtocol;
using SharpDbg.Benchmarks.Reporting;

namespace SharpDbg.Benchmarks.Benchmarks;

public class LaunchBenchmarks : AdapterBenchmark
{
	private DebugSession? _session;

	/// <summary>
	/// From starting the adapter process to the debuggee stopped at a breakpoint on its first line, with the top frame read:
	/// what someone pressing F5 waits for.
	/// </summary>
	[Benchmark]
	[Measures("Starting the adapter process → stopped at a breakpoint on the first line, top frame read")]
	public void LaunchToFirstBreakpoint()
	{
		_session = DebugSession.Start(Definition);
		var stop = _session.LaunchToBreakpoint("launch", new SourceBreakpoint { Line = DebuggeeSource.LaunchBreakpoint.Line });
		_session.ExpectStoppedAt(stop, DebuggeeSource.LaunchBreakpoint);
	}

	[IterationCleanup]
	public void EndSession()
	{
		_session?.Dispose();
		_session = null;
	}
}
