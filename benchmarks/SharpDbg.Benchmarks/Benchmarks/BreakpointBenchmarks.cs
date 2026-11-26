using BenchmarkDotNet.Attributes;
using SharpDbg.DapProtocol;

namespace SharpDbg.Benchmarks.Benchmarks;

public class BreakpointBenchmarks : StoppedSessionBenchmark
{
	private SourceBreakpoint[] _everyLine = [];

	[GlobalSetup(Target = nameof(ContinueToBreakpoint))]
	public void LaunchAtStepStart() => Launch("step", DebuggeeSource.StepStart);

	[GlobalSetup(Target = nameof(ConditionalBreakpoint))]
	public void LaunchAtConditional() => Launch("conditional", DebuggeeSource.ConditionalBreakpoint, condition: "i == 999");

	[GlobalSetup(Target = nameof(SetBreakpointOnEveryLine))]
	public void LaunchAtLaunchBreakpoint()
	{
		Launch("launch", DebuggeeSource.LaunchBreakpoint);
		var lineCount = File.ReadAllLines(DebuggeeSource.ProgramPath).Length;
		_everyLine = Enumerable.Range(1, lineCount).Select(line => new SourceBreakpoint { Line = line }).ToArray();
	}

	/// <summary>Continue from Program.cs:33 round StepLoop's loop to the same breakpoint: the cost of a stop and a resume.</summary>
	[Benchmark]
	public void ContinueToBreakpoint()
	{
		Session.ExpectStoppedAt(Session.ContinueToStop(ThreadId), DebuggeeSource.StepStart);
	}

	/// <summary>Continue to Program.cs:72 with the condition <c>i == 999</c>, which is evaluated 1000 times on the way.</summary>
	[Benchmark]
	public void ConditionalBreakpoint()
	{
		Session.ExpectStoppedAt(Session.ContinueToStop(ThreadId), DebuggeeSource.ConditionalBreakpoint);
	}

	/// <summary>One setBreakpoints request for every line of Program.cs (code or not), while stopped.</summary>
	[Benchmark]
	public void SetBreakpointOnEveryLine()
	{
		var response = Session.SetBreakpoints(_everyLine);
		if (response.Breakpoints.Count != _everyLine.Length) throw new InvalidOperationException($"{Adapter} returned {response.Breakpoints.Count} breakpoints for {_everyLine.Length} requested");
	}

	[IterationCleanup(Target = nameof(SetBreakpointOnEveryLine))]
	public void ClearBreakpoints() => Session.SetBreakpoints(new SourceBreakpoint { Line = DebuggeeSource.LaunchBreakpoint.Line });
}
