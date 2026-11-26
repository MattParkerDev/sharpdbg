using BenchmarkDotNet.Attributes;
using SharpDbg.DapProtocol;

namespace SharpDbg.Benchmarks.Benchmarks;

/// <summary>Runs each benchmark once per configured adapter.</summary>
public abstract class AdapterBenchmark
{
	[ParamsSource(nameof(Adapters))]
	public string Adapter { get; set; } = "";

	public static IEnumerable<string> Adapters => AdapterRegistry.SelectedNames;

	protected AdapterDefinition Definition => AdapterRegistry.Get(Adapter);
}

/// <summary>
/// One debug session per benchmark, launched in global setup and stopped at a breakpoint the debuggee hits over and over.
/// <see cref="ReturnToBreakpoint"/> continues to that breakpoint before each iteration, so every iteration starts from a fresh stop.
/// </summary>
public abstract class StoppedSessionBenchmark : AdapterBenchmark
{
	protected DebugSession Session = null!;
	protected int ThreadId;
	private SourceLine _breakpoint;
	private bool _atBreakpoint;

	protected void Launch(string scenario, SourceLine breakpoint, string? condition = null)
	{
		_breakpoint = breakpoint;
		Session = DebugSession.Start(Definition);
		var stop = Session.LaunchToBreakpoint(scenario, new SourceBreakpoint { Line = breakpoint.Line, Condition = condition });
		Session.ExpectStoppedAt(stop, breakpoint);
		ThreadId = stop.ThreadId!.Value;
		_atBreakpoint = true;
	}

	protected void ReturnToBreakpoint()
	{
		if (_atBreakpoint)
		{
			_atBreakpoint = false;
			return;
		}
		var stop = Session.ContinueToStop(ThreadId);
		Session.ExpectStoppedAt(stop, _breakpoint);
		ThreadId = stop.ThreadId!.Value;
	}

	[GlobalCleanup]
	public void DisposeSession() => Session?.Dispose();
}
