using BenchmarkDotNet.Attributes;
using SharpDbg.DapProtocol;

namespace SharpDbg.Benchmarks.Benchmarks;

/// <summary>
/// What an IDE asks for after a stop, at Program.cs:63 in Recurse, 21 calls deep.
/// Each iteration starts from a new stop, so nothing an adapter caches for a stop carries over.
/// </summary>
public class InspectBenchmarks : StoppedSessionBenchmark
{
	private static readonly string[] Expressions = ["depth + 1", "text.Length", "wide.Field07", "wide.Name", "numbers.Count"];

	private int _frameId;
	private int _localsReference;
	private int _wideReference;
	private int _numbersReference;

	[GlobalSetup]
	public void LaunchAtInspectBreakpoint() => Launch("inspect", DebuggeeSource.InspectBreakpoint);

	[IterationSetup]
	public void StopAtBreakpoint()
	{
		ReturnToBreakpoint();
		_frameId = Session.TopFrame(ThreadId).Id;
		_localsReference = Session.Send(new ScopesArguments { FrameId = _frameId }).Scopes.First().VariablesReference;
		var locals = Session.Send(new VariablesArguments { VariablesReference = _localsReference }).Variables;
		_wideReference = Reference(locals, "wide");
		_numbersReference = Reference(locals, "numbers");
	}

	/// <summary>Every frame of the stopped thread (23 of them).</summary>
	[Benchmark]
	public void StackTrace()
	{
		var frames = Session.Send(new StackTraceArguments { ThreadId = ThreadId, StartFrame = 0 }).StackFrames;
		if (frames.Count < 23) throw new InvalidOperationException($"{Adapter} returned {frames.Count} frames, expected at least 23");
	}

	/// <summary>The scopes of the top frame and the variables of its first scope, as shown when a stop opens the Variables view.</summary>
	[Benchmark]
	public void Locals()
	{
		var scope = Session.Send(new ScopesArguments { FrameId = _frameId }).Scopes.First();
		var locals = Session.Send(new VariablesArguments { VariablesReference = scope.VariablesReference }).Variables;
		Reference(locals, "wide");
	}

	/// <summary>Expanding <c>wide</c>, an object with 20 fields and 6 properties.</summary>
	[Benchmark]
	public void ExpandObject()
	{
		var members = Session.Send(new VariablesArguments { VariablesReference = _wideReference }).Variables;
		if (members.Count < 20) throw new InvalidOperationException($"{Adapter} returned {members.Count} members of wide, expected at least 20");
	}

	/// <summary>Expanding <c>numbers</c>, a <c>List&lt;int&gt;</c> of 1000.</summary>
	[Benchmark]
	public void ExpandList()
	{
		var items = Session.Send(new VariablesArguments { VariablesReference = _numbersReference }).Variables;
		if (items.Count is 0) throw new InvalidOperationException($"{Adapter} returned no children of numbers");
	}

	/// <summary>Five evaluations in the top frame: arithmetic, a string property, a field, an auto-property and a List property.</summary>
	[Benchmark]
	public void Evaluate()
	{
		List<string>? failures = null;
		foreach (var expression in Expressions)
		{
			try
			{
				var response = Session.Send(new EvaluateArguments { Expression = expression, FrameId = _frameId, Context = EvaluateArguments.ContextValue.Watch });
				if (string.IsNullOrEmpty(response.Result)) (failures ??= []).Add($"'{expression}' evaluated to nothing");
			}
			catch (InvalidOperationException ex)
			{
				(failures ??= []).Add($"'{expression}': {ex.Message}");
			}
		}
		if (failures is not null) throw new InvalidOperationException($"{Adapter} could not evaluate:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
	}

	private int Reference(IReadOnlyCollection<Variable> variables, string name)
	{
		// clrdbg names variables with their type, e.g. 'wide [WideObject]'
		var variable = variables.FirstOrDefault(v => v.Name == name || v.Name.StartsWith(name + " [", StringComparison.Ordinal)) ?? throw new InvalidOperationException($"{Adapter} has no local '{name}'. Locals: {string.Join(", ", variables.Select(v => v.Name))}");
		if (variable.VariablesReference is 0) throw new InvalidOperationException($"{Adapter} cannot expand '{name}'");
		return variable.VariablesReference;
	}
}
