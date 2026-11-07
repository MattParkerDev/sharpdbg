using ICorDebugSharp;

namespace SharpDbg.Infrastructure.Debugger.Breakpoints;

/// <summary>
/// The one-shot breakpoint that stops a launched program at its entry point, when the client asked to stop at entry
/// </summary>
/// <remarks>
/// A launch with stop at entry starts <see cref="Armed"/>, and any other session <see cref="NotRequested"/>. <see cref="Armed"/> becomes
/// <see cref="Bound"/> when the module with the entry point loads, and <see cref="Bound"/> becomes <see cref="Consumed"/> when it is hit.
/// </remarks>
internal closed record EntryBreakpointState
{
	/// <summary>The session does not stop at entry</summary>
	public sealed record NotRequested : EntryBreakpointState
	{
		public static NotRequested Instance { get; } = new();
	}

	/// <summary>Waiting for the module that has the entry point to load</summary>
	public sealed record Armed : EntryBreakpointState
	{
		public static Armed Instance { get; } = new();
	}

	/// <summary>Set at the entry point, and not hit yet. User breakpoints at the same location share <paramref name="CorBreakpoint"/></summary>
	public sealed record Bound(ICorDebugFunctionBreakpoint CorBreakpoint, CORDB_ADDRESS ModuleBaseAddress, int MethodToken, int IlOffset) : EntryBreakpointState;

	/// <summary>The program stopped at entry, and the breakpoint is gone</summary>
	public sealed record Consumed : EntryBreakpointState
	{
		public static Consumed Instance { get; } = new();
	}
}
