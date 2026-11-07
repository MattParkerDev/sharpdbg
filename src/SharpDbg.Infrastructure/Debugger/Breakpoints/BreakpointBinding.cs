using System.Collections.Immutable;
using ICorDebugSharp;

namespace SharpDbg.Infrastructure.Debugger.Breakpoints;

/// <summary>
/// Whether a breakpoint is bound to code in the debuggee. The breakpoint is reported to the client as verified only when it is bound
/// </summary>
/// <remarks>
/// Every breakpoint starts <see cref="Unbound"/>. It becomes <see cref="SourceBound"/> or <see cref="FunctionBound"/> when a
/// loaded module has code for it, and goes back to <see cref="Unbound"/> when the module(s) it is bound in unload.
/// A <see cref="Rejected"/> breakpoint never changes state.
/// </remarks>
public closed record BreakpointBinding
{
	/// <summary>Not bound to code yet. Binding is tried again whenever a module with symbols loads</summary>
	public sealed record Unbound(string Message) : BreakpointBinding;

	/// <summary>Can never be bound, e.g. its condition is not supported. Never tried again</summary>
	public sealed record Rejected(string Message) : BreakpointBinding;

	/// <summary>A source breakpoint bound to the location the PDB resolved its line to</summary>
	public sealed record SourceBound(ICorDebugFunctionBreakpoint CorBreakpoint, CORDB_ADDRESS ModuleBaseAddress, ModuleMetadataReader.ResolvedBreakpoint Resolved) : BreakpointBinding;

	/// <summary>A function breakpoint bound to every matching method in the loaded modules. Always has at least one binding</summary>
	public sealed record FunctionBound(ImmutableArray<FunctionBreakpointBinding> Bindings) : BreakpointBinding
	{
		public ImmutableArray<FunctionBreakpointBinding> Bindings { get; } = Bindings.IsDefaultOrEmpty
			? throw new ArgumentException("A bound function breakpoint must have at least one binding", nameof(Bindings))
			: Bindings;
	}
}
