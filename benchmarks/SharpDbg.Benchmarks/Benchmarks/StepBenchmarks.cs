using BenchmarkDotNet.Attributes;
using SharpDbg.Benchmarks.Reporting;

namespace SharpDbg.Benchmarks.Benchmarks;

/// <summary>
/// Each step is timed from the request to the top frame of the new stop being read, which is when an IDE can move its cursor.
/// The debuggee is StepLoop in BenchmarkDebuggee/Program.cs.
/// </summary>
public class StepBenchmarks : StoppedSessionBenchmark
{
	[GlobalSetup(Targets = [nameof(StepOver), nameof(StepIn)])]
	public void LaunchAtStepStart() => Launch("step", DebuggeeSource.StepStart);

	[GlobalSetup(Target = nameof(StepOut))]
	public void LaunchInAdd() => Launch("step", DebuggeeSource.StepOutStart);

	[IterationSetup]
	public void StopAtBreakpoint() => ReturnToBreakpoint();

	/// <summary>Program.cs:34 to 35, over a call to Add.</summary>
	[Benchmark]
	[Measures("Step over a call → the new stop's top frame read")]
	public void StepOver()
	{
		Session.Send(new SharpDbg.DapProtocol.NextArguments { ThreadId = ThreadId });
		Session.ExpectStoppedAt(Session.WaitForStop(), DebuggeeSource.StepOverTarget);
	}

	/// <summary>Program.cs:34 into Add (42-45).</summary>
	[Benchmark]
	[Measures("Step into a call → the new stop's top frame read")]
	public void StepIn()
	{
		Session.Send(new SharpDbg.DapProtocol.StepInArguments { ThreadId = ThreadId });
		Session.ExpectStoppedIn(Session.WaitForStop(), DebuggeeSource.AddBody);
	}

	/// <summary>Program.cs:43 in Add, out to StepLoop (33-37).</summary>
	[Benchmark]
	[Measures("Step out to the caller → the new stop's top frame read")]
	public void StepOut()
	{
		Session.Send(new SharpDbg.DapProtocol.StepOutArguments { ThreadId = ThreadId });
		Session.ExpectStoppedIn(Session.WaitForStop(), DebuggeeSource.StepLoopBody);
	}
}
