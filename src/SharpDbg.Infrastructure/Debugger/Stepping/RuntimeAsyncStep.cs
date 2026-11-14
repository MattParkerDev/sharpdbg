using ICorDebugSharp;

namespace SharpDbg.Infrastructure.Debugger.Stepping;

/// <summary>
/// A runtime-async await can remove the frame and resume on another thread. Keep the activation's identity so the resume breakpoint
/// ends the right step, even with concurrent or recursive calls to the same method
/// </summary>
/// <param name="Type">May be StepOut even for a user step over, when the method has no statement left</param>
/// <param name="Frame">
/// The awaiting frame when <paramref name="FrameAwaits"/> is true; otherwise the callee being left, whose caller is suspended
/// </param>
/// <param name="ResumeBreakpoint">Where the awaiting method goes on after the await, whether it suspended there or not</param>
/// <param name="ResumesPast">Optimized code may have no mapped IL left in the await's statement, putting the breakpoint past it</param>
/// <param name="Chain">
/// A strong handle keeps the chain's identity valid across GC. Null if it could not be read or the chain has not suspended yet
/// </param>
/// <param name="Waiters">
/// Physical and suspended callers together distinguish recursive activations in the same chain. Null when the count could not be read
/// </param>
public sealed record RuntimeAsyncStep(
	int ThreadId,
	StepType Type,
	(CORDB_ADDRESS Start, CORDB_ADDRESS End) Frame,
	CORDB_ADDRESS Module,
	int Method,
	bool FrameAwaits,
	ICorDebugFunctionBreakpoint ResumeBreakpoint,
	bool ResumesPast,
	ICorDebugHandleValue? Chain,
	int? Waiters)
{
	/// <summary>Whether the stepping thread has left the awaiting frame at the await, which then suspended</summary>
	public bool Left { get; init; }

	/// <summary>
	/// A new chain has no task until it suspends. This hook captures its identity before we wait for it to resume;
	/// null once captured, or when the hook is unavailable
	/// </summary>
	public ICorDebugFunctionBreakpoint? SuspendBreakpoint { get; init; }

	/// <summary>
	/// A step out that returns before the caller's await must finish that await, but leave the rest of the statement available to step into
	/// </summary>
	public bool StopAfterAwait { get; init; }

	public bool StopsAtResume => Type is StepType.StepOut || StopAfterAwait;

	/// <summary>
	/// Stack addresses can be reused after the frame leaves, so also check the method and module
	/// </summary>
	public bool IsFrame(ICorDebugFrame? frame)
	{
		if (frame is null) return false;
		try
		{
			return frame.StackRange == Frame && frame.TryGetFunction(out var function) is Cor.S_OK
				&& (int)function.Token == Method && function.Module.BaseAddress == Module;
		}
		catch (Exception)
		{
			return false;
		}
	}
}
