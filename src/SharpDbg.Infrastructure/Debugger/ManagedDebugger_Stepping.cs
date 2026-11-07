using ICorDebugSharp;
using SharpDbg.Infrastructure.Debugger.Stepping;

namespace SharpDbg.Infrastructure.Debugger;

public partial class ManagedDebugger
{
	private StepState _step = StepState.Idle.Instance;

	private void StartStep(StepState step)
	{
		if (_step is not StepState.Idle) throw new InvalidOperationException("A step operation is already in progress");
		_step = step;
	}

	/// <summary>
	/// End the step in progress, if any. This is the only place that deactivates a step's stepper and breakpoint
	/// </summary>
	private void EndStep()
	{
		var step = _step;
		_step = StepState.Idle.Instance;
		step.CorStepper?.TryDeactivate();
		if (step.CorBreakpoint is { } corBreakpoint) DeactivateIfUnused(corBreakpoint);
		if (step is StepState.AwaitingResume { AsyncId: var asyncId })
		{
			try
			{
				asyncId.Dispose();
			}
			catch
			{
				// Ignore handle cleanup errors
			}
		}
	}

	/// <summary>
	/// Start a step requested by the user, and continue
	/// </summary>
	private async Task StartUserStep(int threadId, StepType stepType)
	{
		if (_run is not RunState.Stopped)
		{
			_logger?.Invoke("Ignoring the step, as the debuggee is not stopped");
			return;
		}
		if (_threads.TryGetValue(threadId, out var threadInfo) is false) return;
		var thread = threadInfo.Thread;
		if (thread.ActiveFrame is not ICorDebugILFrame) throw new InvalidOperationException("Active frame is not an IL frame");
		if (_step is not StepState.Idle) throw new InvalidOperationException("A step operation is already in progress");

		StartStep(await _asyncStepper.TryCreateAsyncStep(thread, stepType) ?? new StepState.Simple(CreateStepper(thread, stepType)));
		ContinueFromStop();
	}

	/// <summary>
	/// Handle a hit of the step's own breakpoint. Returns false when the breakpoint is not the step's, which leaves the step as it was
	/// </summary>
	private async Task<bool> TryHandleStepBreakpoint(ICorDebugThread thread, ICorDebugFunctionBreakpoint breakpoint)
	{
		switch (_step)
		{
			// A user breakpoint that shares the CLR breakpoint handles the hit instead
			case StepState.RunToStateMachineStart runToStart when runToStart.Breakpoint == breakpoint && _breakpointManager.FindAllByCorBreakpoint(breakpoint).Count is 0:
			{
				// Another invocation of the same async method started on another thread first: keep waiting for the stepping thread's
				if (thread.Id != runToStart.ThreadId) break;
				EndStep();
				if (TrySetupStepToFirstLine(thread)) break;
				ReportStopAtActiveFrame(thread, "step");
				return true;
			}
			case StepState.AwaitingYield awaitingYield when awaitingYield.YieldBreakpoint == breakpoint:
			{
				// Another activation yielding: keep waiting for the stepping one, whose step carries on
				if (thread.Id != awaitingYield.ThreadId || thread.ActiveFrame is not ICorDebugILFrame frame || frame.StackRange != awaitingYield.FrameStackRange) break;
				EndStep();
				StartStep(await _asyncStepper.CreateAwaitingResume(frame, awaitingYield));
				break;
			}
			case StepState.AwaitingResume awaitingResume when awaitingResume.ResumeBreakpoint == breakpoint:
			{
				if (await _asyncStepper.IsResumingSteppingActivation(thread, awaitingResume) is false) break;
				EndStep();
				SetupStepper(thread, awaitingResume.StepType);
				break;
			}
			case StepState.AwaitingTaskCompletion awaitingTaskCompletion when awaitingTaskCompletion.NotifyBreakpoint == breakpoint:
			{
				EndStep();
				// The task-completion hook runs inside the awaiting caller's GetResult: step out to that caller, then finish its
				// await statement. Stepping is exact here so it lands in the same place whether or not Just My Code is enabled
				StartStep(TryCreateStepOutToAwaitingUserFrame(thread) ?? new StepState.Simple(CreateStepper(thread, StepType.StepIn)));
				break;
			}
			default:
				return false;
		}
		ContinueWithVariableClear();
		return true;
	}

	/// <summary>
	/// Start a simple step, without continuing execution
	/// </summary>
	private void SetupStepper(ICorDebugThread thread, StepType stepType) => StartStep(new StepState.Simple(CreateStepper(thread, stepType)));

	/// <summary>
	/// Create and activate a stepper, without continuing execution
	/// </summary>
	internal ICorDebugStepper CreateStepper(ICorDebugThread thread, StepType stepType)
	{
		var frame = thread.ActiveFrame;
		if (frame is not ICorDebugILFrame ilFrame) throw new InvalidOperationException("Active frame is not an IL frame");

		ICorDebugStepper stepper = frame.CreateStepper();
		stepper.SetInterceptMask(CorDebugIntercept.INTERCEPT_ALL & ~(CorDebugIntercept.INTERCEPT_SECURITY | CorDebugIntercept.INTERCEPT_CLASS_INIT));
		stepper.SetUnmappedStopMask(CorDebugUnmappedStop.STOP_NONE);
		if (_justMyCode) stepper.SetJMC(true);

		if (stepType == StepType.StepOut)
		{
			stepper.StepOut();
		}
		else // StepIn or StepOver
		{
			var metadataReader = _modules[frame.Function.Module.BaseAddress].MetadataReader;

			var currentIlOffset = ilFrame.IP.pnOffset;
			var nullableResult = metadataReader.GetStartAndEndSequencePointIlOffsetsForIlOffset(frame.Function.Token, currentIlOffset);
			if (nullableResult is var (startIlOffset, endIlOffset))
			{
				if (startIlOffset == endIlOffset)
				{
					endIlOffset = frame.Function.ILCode.Size;
				}
				var stepRange = new COR_DEBUG_STEP_RANGE
				{
					startOffset = checked((uint)startIlOffset),
					endOffset = checked((uint)endIlOffset)
				};
				var stepIn = stepType is StepType.StepIn;
				stepper.StepRange(stepIn, [stepRange], 1);
			}
			else
			{
				var stepIn = stepType is StepType.StepIn;
				stepper.Step(stepIn);
			}
		}

		return stepper;
	}

	/// <summary>
	/// Step out of every frame above the first user code frame on the stack, without Just My Code so the step stops exactly there
	/// </summary>
	private StepState? TryCreateStepOutToAwaitingUserFrame(ICorDebugThread thread)
	{
		ICorDebugILFrame? callee = null;
		foreach (var frame in EnumerateFramesForThread(thread))
		{
			if (frame is not ICorDebugILFrame ilFrame) continue;
			if (callee is not null && _modules.TryGetValue(ilFrame.Function.Module.BaseAddress, out var module) && module.IsUserCode && module.MetadataReader.HasSymbols)
			{
				var stepper = callee.CreateStepper();
				stepper.SetInterceptMask(CorDebugIntercept.INTERCEPT_ALL & ~(CorDebugIntercept.INTERCEPT_SECURITY | CorDebugIntercept.INTERCEPT_CLASS_INIT));
				stepper.SetUnmappedStopMask(CorDebugUnmappedStop.STOP_NONE);
				stepper.StepOut();
				return new StepState.StepOutToAwaitingUserFrame(stepper);
			}
			callee = ilFrame;
		}
		return null;
	}

	/// <summary>
	/// Step out of an async state machine that has not yielded yet, i.e. whose kick-off method is still on the stack, to the kick-off
	/// method's caller: until its first yield an async method is an ordinary call. Returns null once the method has resumed after an await
	/// </summary>
	/// <remarks>
	/// The step leaves the kick-off method exactly, without Just My Code, so it does not stop in the builder's Start or in the kick-off
	/// method's hidden code. Once back in the caller: if the caller's statement goes on to await, the step finishes the statement (see
	/// <see cref="TryFinishAwaitingStatement"/>), so <c>await M()</c> lands after the await as when M has yielded, while
	/// <c>var task = M();</c> stops on that line; and with Just My Code, a caller that is not user code is stepped out of as usual
	/// </remarks>
	internal StepState? TryCreateStepOutBeforeFirstYield(ICorDebugThread thread, CORDB_ADDRESS moduleAddress, int kickoffMethodToken)
	{
		if (kickoffMethodToken is 0) return null;
		var kickoffFrame = FindKickoffFrame(thread, moduleAddress, kickoffMethodToken);
		if (kickoffFrame is null) return null;
		var stepper = kickoffFrame.CreateStepper();
		stepper.SetInterceptMask(CorDebugIntercept.INTERCEPT_ALL & ~(CorDebugIntercept.INTERCEPT_SECURITY | CorDebugIntercept.INTERCEPT_CLASS_INIT));
		stepper.SetUnmappedStopMask(CorDebugUnmappedStop.STOP_NONE);
		stepper.StepOut();
		return new StepState.StepOutToKickoffCaller(stepper);
	}

	/// <summary>
	/// Without Just My Code, a step into an async method that lands in its kick-off method runs to the start of the state machine's
	/// MoveNext instead of stepping on, then steps to its first line (<see cref="TrySetupStepToFirstLine"/>)
	/// </summary>
	/// <remarks>
	/// The kick-off method has no source, so the step would carry on into what it calls: the state machine's constructor (also without
	/// source), then Object..ctor and the builder's Create and Start, which do have decompiled source and would each end the step there,
	/// and stepping over Start steps over MoveNext. Running to MoveNext works whatever the builder is, and finds MoveNext from metadata,
	/// so it also works for a module without a PDB, which is decompiled once the step reaches it. With Just My Code the stepper skips all
	/// of that by itself
	/// </remarks>
	private bool TrySetupRunToStateMachineStart(ICorDebugThread thread, ICorDebugILFrame frame)
	{
		if (_justMyCode) return false;
		var function = frame.Function;
		if (_modules.TryGetValue(function.Module.BaseAddress, out var module) is false) return false;
		if (module.MetadataReader.GetAsyncStateMachineMoveNextMethod(function.Token) is not { } moveNextToken) return false;

		var breakpoint = function.Module.GetFunctionFromToken(moveNextToken).ILCode.CreateBreakpoint(0);
		breakpoint.Activate(true);
		StartStep(new StepState.RunToStateMachineStart(breakpoint, thread.Id));
		return true;
	}

	/// <summary>
	/// At the start of MoveNext, before its state dispatch: step over that dispatch to the method's first line. Returns false when the
	/// step is already there
	/// </summary>
	private bool TrySetupStepToFirstLine(ICorDebugThread thread)
	{
		if (thread.ActiveFrame is not ICorDebugILFrame ilFrame) return false;
		// Decompiles the module first if it has no PDB
		GetSourceInfoAtFrame(ilFrame, _justMyCode is false);
		var function = ilFrame.Function;
		var metadataReader = _modules[function.Module.BaseAddress].MetadataReader;
		var ip = ilFrame.IP.pnOffset;
		if (metadataReader.GetNextUserCodeIlOffset(function.Token, ip) is not { } firstLineOffset)
		{
			// No source for MoveNext either: step on as from any other method without source
			SetupStepper(thread, StepType.StepIn);
			return true;
		}
		if (firstLineOffset == ip) return false;

		var stepper = ilFrame.CreateStepper();
		stepper.SetInterceptMask(CorDebugIntercept.INTERCEPT_ALL & ~(CorDebugIntercept.INTERCEPT_SECURITY | CorDebugIntercept.INTERCEPT_CLASS_INIT));
		stepper.SetUnmappedStopMask(CorDebugUnmappedStop.STOP_NONE);
		stepper.StepRange(false, [new COR_DEBUG_STEP_RANGE { startOffset = checked((uint)ip), endOffset = checked((uint)firstLineOffset) }], 1);
		StartStep(new StepState.Simple(stepper));
		return true;
	}

	private ICorDebugILFrame? FindKickoffFrame(ICorDebugThread thread, CORDB_ADDRESS moduleAddress, int kickoffMethodToken) =>
		EnumerateFramesForThread(thread).OfType<ICorDebugILFrame>().Skip(1)
			.FirstOrDefault(frame => frame.Function.Module.BaseAddress == moduleAddress && frame.Function.Token == kickoffMethodToken);

	/// <summary>
	/// Back in the caller of an async method that had not yielded, or on a hidden sequence point: if the code before the next visible
	/// sequence point awaits, step over it, crossing the await, rather than stopping part way through the statement
	/// </summary>
	private async Task<bool> TryFinishAwaitingStatement(ICorDebugThread thread)
	{
		if (thread.ActiveFrame is not ICorDebugILFrame ilFrame) return false;
		var function = ilFrame.Function;
		if (_modules.TryGetValue(function.Module.BaseAddress, out var module) is false) return false;
		var metadataReader = module.MetadataReader;
		var asyncInfo = metadataReader.GetAsyncMethodSteppingInfo(function.Token);
		if (asyncInfo is null) return false;
		var ip = ilFrame.IP.pnOffset;
		if (metadataReader.GetStartAndEndSequencePointIlOffsetsForIlOffset(function.Token, ip) is not var (_, statementEnd)) return false;
		if (asyncInfo.AwaitInfos.Any(info => info.YieldOffset >= ip && info.YieldOffset < statementEnd) is false) return false;

		StartStep(await _asyncStepper.TryCreateAsyncStep(thread, StepType.StepOver) ?? new StepState.Simple(CreateStepper(thread, StepType.StepOver)));
		return true;
	}
}
