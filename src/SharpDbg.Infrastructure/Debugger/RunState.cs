namespace SharpDbg.Infrastructure.Debugger;

/// <summary>
/// Whether the debuggee is running, or stopped with the client told so
/// </summary>
/// <remarks>
/// The debuggee is <see cref="Running"/> until a stop is reported to the client, which makes it <see cref="Stopped"/>. A continue or
/// step from the client makes it <see cref="Running"/> again. While runtime events are handled the runtime is briefly stopped too,
/// but that is not a stop: every handler ends by continuing or reporting a stop. Variable and frame references are only valid while
/// <see cref="Stopped"/>.
/// </remarks>
public closed record RunState
{
	public sealed record Running : RunState
	{
		public static Running Instance { get; } = new();
	}

	/// <param name="ThreadId">The thread the stop was reported on</param>
	/// <param name="Reason">The DAP stop reason, e.g. "breakpoint"</param>
	public sealed record Stopped(int ThreadId, string Reason) : RunState;
}
