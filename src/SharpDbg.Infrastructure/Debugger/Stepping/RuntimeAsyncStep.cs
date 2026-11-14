using ICorDebugSharp;

namespace SharpDbg.Infrastructure.Debugger.Stepping;

/// <summary>
/// A step through an await of a runtime-async method (.NET 11), which has no state machine: an await that suspends returns from the
/// method's frame, and the runtime later calls the method again where it goes on, on whichever thread. The step waits there, at the
/// IL after the await, for its own run to go on
/// </summary>
/// <param name="ThreadId">The stepping thread</param>
/// <param name="Type">The step the user asked for. A step out, or a step past a method's end, is a step out to the caller's await</param>
/// <param name="Frame">
/// The step's frame on the stepping thread, with <paramref name="Module"/> and <paramref name="Method"/>: the awaiting one
/// (<paramref name="FrameAwaits"/>) until it leaves at the await, or the one a step out leaves, whose caller waits in a continuation
/// </param>
/// <param name="ResumeBreakpoint">Where the awaiting method goes on after the await, whether it suspended there or not</param>
/// <param name="ResumesPast">Whether that is past the await's statement: optimized code (the libraries') leaves nothing of the statement after its await</param>
/// <param name="Chain">
/// A strong handle to the task whose chain of continuations the awaiting run is in, which the runtime's dispatcher runs; null for a run
/// whose chain has not suspended yet (a chain is given its task when it first suspends)
/// </param>
/// <param name="Waiters">
/// How many runs wait for the awaiting run, in its chain: its awaiting callers on the stack, and the continuations after it. Tells it from
/// a run of the same method in the same chain, e.g. a recursive call's. Null when it could not be told
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
	/// Whether the step goes through the await alone, and stops where the awaiting method goes on after it, on the await's line, as a step out
	/// of a synchronous call stops on the call's line: a step out, or the end of one that returned to a caller still before its await
	/// </summary>
	public bool StopAfterAwait { get; init; }

	/// <summary>Whether the step stops where the awaiting method goes on after the await, rather than stepping on from there</summary>
	public bool StopsAtResume => Type is StepType.StepOut || StopAfterAwait;

	/// <summary>
	/// Whether a frame is the step's: at its place on the stack and of its method, as another method's frame can take that place once
	/// the step's has gone (on x64 a frame's place is a single stack address)
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
