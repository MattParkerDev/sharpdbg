using BenchmarkDotNet.Attributes;
using SharpDbg.DapProtocol;
using SharpDbg.Benchmarks.Reporting;

namespace SharpDbg.Benchmarks.Benchmarks;

/// <summary>
/// <see cref="InspectBenchmarks.Locals"/> for locals whose values a debugger can show without running code in the debuggee,
/// at Program.cs:91 in PlainLocals: an int, a string, a class and a struct, none with [DebuggerDisplay] or a ToString override.
/// A debugger that evaluates display values and one that only prints type names should then cost about the same.
/// </summary>
[Group("Inspect")]
public class PlainLocalsBenchmarks : StoppedSessionBenchmark
{
	private int _frameId;

	[GlobalSetup]
	public void LaunchAtPlainInspectBreakpoint() => Launch("inspect-plain", DebuggeeSource.PlainInspectBreakpoint);

	[IterationSetup]
	public void StopAtBreakpoint()
	{
		ReturnToBreakpoint();
		_frameId = Session.TopFrame(ThreadId).Id;
		// The same requests as InspectBenchmarks' setup, so both Locals benchmarks time the second listing at a stop
		var scope = Session.Send(new ScopesArguments { FrameId = _frameId }).Scopes.First();
		Session.Send(new VariablesArguments { VariablesReference = scope.VariablesReference });
	}

	/// <summary>The scopes of the top frame and the variables of its first scope.</summary>
	[Benchmark]
	[Measures("Locals, with no [DebuggerDisplay], ToString override or collection")]
	public void LocalsPlain()
	{
		var scope = Session.Send(new ScopesArguments { FrameId = _frameId }).Scopes.First();
		var locals = Session.Send(new VariablesArguments { VariablesReference = scope.VariablesReference }).Variables;
		foreach (var name in (string[])["count", "plain", "point", "text", "total"])
		{
			// clrdbg names variables with their type, e.g. 'plain [PlainObject]'
			if (locals.Any(v => v.Name == name || v.Name.StartsWith(name + " [", StringComparison.Ordinal)) is false)
			{
				throw new InvalidOperationException($"{Adapter} has no local '{name}'. Locals: {string.Join(", ", locals.Select(v => v.Name))}");
			}
		}
	}
}
