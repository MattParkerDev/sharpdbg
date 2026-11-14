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
/// <see cref="StepOutToAwaitingUserFrame"/>, then stops on the caller's await line to leave the rest of the statement available to step into.
/// Before the first yield, <see cref="StepOutToKickoffCaller"/> returns to a caller whose await may still need to finish, using
/// <see cref="AwaitingYield"/>, <see cref="AwaitingResume"/> and <see cref="FinishAwait"/> as needed.
/// <see cref="RuntimeAsyncAwait"/> drops its stepper when the frame leaves, since that thread may then run unrelated code
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
	/// Ends a step out just after GetResult, leaving the rest of the caller's statement available to step into.
	/// Null for a step over or into the whole statement
	/// </param>
	public sealed record AwaitingYield(ICorDebugStepper Stepper, ICorDebugFunctionBreakpoint YieldBreakpoint, int ThreadId, (CORDB_ADDRESS Start, CORDB_ADDRESS End) FrameStackRange, StepType StepType, uint ResumeOffset, uint? StopOffset = null) : StepState
	{
		/// <summary>
		/// Synchronous completion skips the yield breakpoint. This breakpoint lets the step re-arm for another await in the same statement
		/// </summary>
		public ICorDebugFunctionBreakpoint? CompletedBreakpoint { get; init; }
	}

	/// <summary>The method has yielded at the await: waiting for it to resume after the await, on any thread</summary>
	/// <param name="AsyncId">A strong handle to the builder's ObjectIdForDebugger, which tells the stepping activation from other invocations of the method</param>
	/// <param name="StopOffset">As <see cref="AwaitingYield.StopOffset"/></param>
	public sealed record AwaitingResume(ICorDebugFunctionBreakpoint ResumeBreakpoint, int ThreadId, StepType StepType, ICorDebugHandleValue AsyncId, uint? StopOffset = null) : StepState;

	/// <summary>
	/// The resume breakpoint is before GetResult; finish that call before reporting the step out on the caller's await line
	/// </summary>
	public sealed record FinishAwait(ICorDebugStepper Stepper) : StepState;

	/// <summary>An async step out after the method has resumed: waiting for its task to complete, in Task.NotifyDebuggerOfWaitCompletion</summary>
	public sealed record AwaitingTaskCompletion(ICorDebugFunctionBreakpoint NotifyBreakpoint) : StepState;

	/// <summary>
	/// The stepper is only useful while the stepping frame is on the stack. Once it leaves, the resume breakpoint tracks the activation
	/// across threads; a step out to a suspended caller needs only that breakpoint from the start
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
