using BenchmarkDotNet.Attributes;
using SharpDbg.DapProtocol;
using SharpDbg.Benchmarks.Reporting;

namespace SharpDbg.Benchmarks.Benchmarks;

/// <summary>
/// Expanding arrays of 100 objects with a [DebuggerDisplay], at Program.cs:133 in DisplayScenario.Stop.
/// One display uses only member names ('{Name,nq} = {Value}'), the other expressions ('{Name + "!",nq} = {Value + 1}').
/// </summary>
[Group("Inspect")]
public class DisplayBenchmarks : StoppedSessionBenchmark
{
	private int _identifiersReference;
	private int _expressionsReference;

	[GlobalSetup]
	public void LaunchAtDisplayBreakpoint() => Launch("inspect-display", DebuggeeSource.DisplayBreakpoint);

	[IterationSetup]
	public void StopAtBreakpoint()
	{
		ReturnToBreakpoint();
		var frameId = Session.TopFrame(ThreadId).Id;
		var scope = Session.Send(new ScopesArguments { FrameId = frameId }).Scopes.First();
		var locals = Session.Send(new VariablesArguments { VariablesReference = scope.VariablesReference }).Variables;
		_identifiersReference = locals.Single(v => v.Name == "identifiers").VariablesReference;
		_expressionsReference = locals.Single(v => v.Name == "expressions").VariablesReference;
	}

	[Benchmark]
	[Measures("Expanding 100 objects whose [DebuggerDisplay] uses member names")]
	public void ExpandDisplayIdentifiers() => Expand(_identifiersReference, "item 5 = 5");

	[Benchmark]
	[Measures("Expanding 100 objects whose [DebuggerDisplay] uses expressions")]
	public void ExpandDisplayExpressions() => Expand(_expressionsReference, "item 5! = 6");

	private void Expand(int reference, string expectedValue)
	{
		var items = Session.Send(new VariablesArguments { VariablesReference = reference }).Variables;
		if (items.Count < 100 || items.Any(v => v.Value == expectedValue) is false)
		{
			throw new InvalidOperationException($"{Adapter} returned {items.Count} items, e.g. '{items.Skip(5).FirstOrDefault()?.Value}', expected 100 with '{expectedValue}'");
		}
	}
}
