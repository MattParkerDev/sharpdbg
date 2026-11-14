using ICorDebugSharp;

namespace SharpDbg.Infrastructure.Debugger.Stepping;

/// <summary>
/// What the step in progress is waiting for before it stops, or steps on
/// </summary>
/// <remarks>
/// A step starts from <see cref="Idle"/>, and goes back to it when it stops, or when something else stops the debuggee: a
/// breakpoint, an exception or a pause. Most steps are one <see cref="Simple"/> step after another, until one lands on a line.
/// Stepping over an await goes <see cref="AwaitingYield"/>, <see cref="AwaitingResume"/>, then <see cref="Simple"/>. Stepping out
/// of an async method that has resumed after an await goes <see cref="AwaitingTaskCompletion"/>,
/// <see cref="StepOutToAwaitingUserFrame"/>, then stops on the awaiting caller's await line, as a step out of a synchronous call stops on
/// the call's line. Stepping out of an async method that has not yielded yet, to a caller whose statement awaits it, goes
/// <see cref="StepOutToKickoffCaller"/>, then through that await alone: <see cref="AwaitingYield"/>, <see cref="AwaitingResume"/> and
/// <see cref="FinishAwait"/> with a stop offset. A step through an await of a runtime-async method (.NET 11) is a
/// <see cref="RuntimeAsyncAwait"/>, with its stepper until the stepping frame leaves at the await, then without, then <see cref="Simple"/>
/// </remarks>
public closed record StepState
{
	/// <summary>No step in progress</summary>
	public sealed record Idle : StepState
	{
		public static Idle Instance { get; } = new();
	}

	/// <summary>A CLR step, which ends at the next line, or is followed by another step if it lands somewhere the user cannot see</summary>
	public sealed record Simple(ICorDebugStepper Stepper) : StepState;

	/// <summary>An async step out after the awaited task completed: stepping out to the first user code frame, the awaiting caller</summary>
	public sealed record StepOutToAwaitingUserFrame(ICorDebugStepper Stepper) : StepState;

	/// <summary>Stepping out of an async method that has not yielded yet, to its kick-off method's caller</summary>
	public sealed record StepOutToKickoffCaller(ICorDebugStepper Stepper) : StepState;

	/// <summary>A step into an async method, without Just My Code, running to the start of its state machine's MoveNext</summary>
	public sealed record RunToStateMachineStart(ICorDebugFunctionBreakpoint Breakpoint, int ThreadId) : StepState;

	/// <summary>A step over or into the next await: a step runs as usual, until the method yields at the await</summary>
	/// <param name="FrameStackRange">
	/// The stepping activation's MoveNext frame. Other activations of the same method run the same code, so they hit the yield
	/// breakpoint too: a recursive call reaches its own await on this thread, deeper in the stack, before the stepping one does
	/// </param>
	/// <param name="StepType">The step to take once the method resumes after the await</param>
	/// <param name="StopOffset">
	/// For a step that goes through the await alone (the end of a step out): the IL offset just after the awaiter's GetResult, where the
	/// step stops, on the await's line, whether or not the method yields. Null for a step over or into the whole statement
	/// </param>
	public sealed record AwaitingYield(ICorDebugStepper Stepper, ICorDebugFunctionBreakpoint YieldBreakpoint, int ThreadId, (CORDB_ADDRESS Start, CORDB_ADDRESS End) FrameStackRange, StepType StepType, uint ResumeOffset, uint? StopOffset = null) : StepState
	{
		/// <summary>
		/// For a step over or into the whole statement: where the await goes on to its awaiter's GetResult whether it yielded or not. Hit
		/// there without yielding, the await was done at once, and the step goes on to follow any await still to come in the statement
		/// </summary>
		public ICorDebugFunctionBreakpoint? CompletedBreakpoint { get; init; }
	}

	/// <summary>The method has yielded at the await: waiting for it to resume after the await, on any thread</summary>
	/// <param name="AsyncId">A strong handle to the builder's ObjectIdForDebugger, which tells the stepping activation from other invocations of the method</param>
	/// <param name="StopOffset">As <see cref="AwaitingYield.StopOffset"/></param>
	public sealed record AwaitingResume(ICorDebugFunctionBreakpoint ResumeBreakpoint, int ThreadId, StepType StepType, ICorDebugHandleValue AsyncId, uint? StopOffset = null) : StepState;

	/// <summary>
	/// The method has resumed after the await of a step that goes through the await alone: stepping to just after the awaiter's GetResult,
	/// where the step stops, on the await's line
	/// </summary>
	public sealed record FinishAwait(ICorDebugStepper Stepper) : StepState;

	/// <summary>An async step out after the method has resumed: waiting for its task to complete, in Task.NotifyDebuggerOfWaitCompletion</summary>
	public sealed record AwaitingTaskCompletion(ICorDebugFunctionBreakpoint NotifyBreakpoint) : StepState;

	/// <summary>
	/// A step through an await of a runtime-async method, waiting for the awaiting method to go on after it: its stepper runs until the
	/// stepping frame leaves at the await (null once it has, or for a step out of a resumed run, which runs free to the waiting caller)
	/// </summary>
	public sealed record RuntimeAsyncAwait(ICorDebugStepper? Stepper, RuntimeAsyncStep Step) : StepState;

	public ICorDebugStepper? CorStepper => this switch
	{
		Idle or RunToStateMachineStart or AwaitingResume or AwaitingTaskCompletion => null,
		Simple step => step.Stepper,
		FinishAwait step => step.Stepper,
		RuntimeAsyncAwait step => step.Stepper,
		StepOutToAwaitingUserFrame step => step.Stepper,
		StepOutToKickoffCaller step => step.Stepper,
		AwaitingYield step => step.Stepper
	};

	public ICorDebugFunctionBreakpoint? CorBreakpoint => this switch
	{
		Idle or Simple or FinishAwait or StepOutToAwaitingUserFrame or StepOutToKickoffCaller => null,
		RunToStateMachineStart step => step.Breakpoint,
		AwaitingYield step => step.YieldBreakpoint,
		AwaitingResume step => step.ResumeBreakpoint,
		AwaitingTaskCompletion step => step.NotifyBreakpoint,
		RuntimeAsyncAwait step => step.Step.ResumeBreakpoint
	};
}
