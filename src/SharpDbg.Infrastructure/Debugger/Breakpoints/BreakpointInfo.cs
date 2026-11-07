using ICorDebugSharp;

namespace SharpDbg.Infrastructure.Debugger.Breakpoints;

public class BreakpointInfo
{
	public const string NotProcessedMessage = "Breakpoint has not been processed by the debugger.";

	public int Id { get; init; }
	public string FilePath { get; init; } = string.Empty;
	/// <summary>The line the client asked for, which binding resolves to the nearest line with code</summary>
	public int RequestedLine { get; init; }
	public int? RequestedColumn { get; init; }
	public string? FunctionName { get; init; }
	public bool IsFunctionBreakpoint => FunctionName is not null;

	public BreakpointBinding Binding
	{
		get;
		set
		{
			if (field is BreakpointBinding.Rejected) throw new InvalidOperationException($"Breakpoint {Id} was rejected, so it cannot change state");
			if (value is BreakpointBinding.SourceBound && IsFunctionBreakpoint || value is BreakpointBinding.FunctionBound && IsFunctionBreakpoint is false)
				throw new InvalidOperationException($"Breakpoint {Id} cannot be bound as a {(IsFunctionBreakpoint ? "source" : "function")} breakpoint");
			field = value;
		}
	} = new BreakpointBinding.Unbound(NotProcessedMessage);

	public bool Verified => Binding is BreakpointBinding.SourceBound or BreakpointBinding.FunctionBound;

	public string? Message => Binding switch
	{
		BreakpointBinding.Unbound unbound => unbound.Message,
		BreakpointBinding.Rejected rejected => rejected.Message,
		BreakpointBinding.SourceBound or BreakpointBinding.FunctionBound => null
	};

	// The resolved location once bound, otherwise the requested one
	public int Line => Binding is BreakpointBinding.SourceBound bound ? bound.Resolved.StartLine : RequestedLine;
	public int? Column => Binding is BreakpointBinding.SourceBound bound ? bound.Resolved.StartColumn : RequestedColumn;
	public int? EndLine => Binding is BreakpointBinding.SourceBound bound ? bound.Resolved.EndLine : null;
	public int? EndColumn => Binding is BreakpointBinding.SourceBound bound ? bound.Resolved.EndColumn : null;

	public IEnumerable<ICorDebugFunctionBreakpoint> CorBreakpoints => Binding switch
	{
		BreakpointBinding.SourceBound bound => [bound.CorBreakpoint],
		BreakpointBinding.FunctionBound bound => bound.Bindings.Select(binding => binding.CorBreakpoint),
		BreakpointBinding.Unbound or BreakpointBinding.Rejected => []
	};

	/// <summary>Conditional expression to evaluate when breakpoint is hit</summary>
	public string? Condition { get; set; }

	/// <summary>Hit count condition (e.g., ">=10", "==5", "%3")</summary>
	public string? HitCondition { get; set; }

	/// <summary>Current hit count for this breakpoint</summary>
	public int HitCount { get; set; }
}
