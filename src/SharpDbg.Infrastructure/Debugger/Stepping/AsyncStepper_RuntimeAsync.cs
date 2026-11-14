using ICorDebugSharp;

namespace SharpDbg.Infrastructure.Debugger.Stepping;

/// <summary>
/// Steps through the awaits of runtime-async methods (.NET 11), which have no state machine. Ported from Passenger's AsyncStepper_RuntimeAsync
/// </summary>
public partial class AsyncStepper
{
	/// <summary>
	/// An await can remove the stepping frame, so the step needs a breakpoint where the awaiting method resumes.
	/// At the end of the method, use the caller's await instead. Null when a plain step is sufficient
	/// </summary>
	/// <param name="stopAfterAwait">
	/// Whether a step over the frame's own await goes through that await alone, and stops after it on its line: the end of a step out that
	/// returned to a caller still before its await
	/// </param>
	public async Task<StepState?> TryCreateRuntimeAsyncStep(ICorDebugThread thread, ICorDebugILFrame frame, StepType type, bool stopAfterAwait = false)
	{
		try
		{
			if (IsRuntimeAsync(frame) is false) return null;
			if (type is not StepType.StepOut && AwaitIn(frame) is { } awaited)
			{
				if (ResumeOf(frame, awaited) is not { } resume) return null;
				if (resume != ModuleMetadataReader.Returns)
				{
					var ownAwait = Wait(thread, type, type, frame, frame, awaited, resume, frameAwaits: true, Waiters(thread, frame));
					return stopAfterAwait && ownAwait is not null ? ownAwait with { Step = ownAwait.Step with { StopAfterAwait = true } } : ownAwait;
				}
			}
			else if (type is not StepType.StepOut && LeavesMethod(frame) is false)
			{
				return null;
			}

			// A run not yet suspended has its caller on the stack; a resumed one, in the continuation its dispatcher goes on with
			if (ManagedDebugger.EnumerateFramesForThread(thread).Skip(1).FirstOrDefault() is ICorDebugILFrame caller && IsRuntimeAsyncCode(caller)
			    && AwaitOfCall(caller) is { } called && ResumeOf(caller, called) is { } callerResumes and >= 0)
			{
				return Wait(thread, type, StepType.StepOut, caller, caller, called, callerResumes, frameAwaits: true, Waiters(thread, caller));
			}
			if (WaitingCallerOf(thread, frame) is { } waiting && AwaitIn(waiting) is { } waited && ResumeOf(waiting, waited) is { } waitingResumes and >= 0)
			{
				// The frame's own waiters count the waiting caller, which waits for one fewer
				return Wait(thread, type, StepType.StepOut, frame, waiting, waited, waitingResumes, frameAwaits: false, Waiters(thread, frame) - 1);
			}
			return await TryWaitForChainsTask(thread, frame);
		}
		catch (Exception ex)
		{
			_debugger.Log($"A runtime-async step could not be set up, so the step is a plain one: {ex.Message}");
			return null;
		}
	}

	/// <summary>
	/// A task awaited separately (<c>var task = WorkAsync(); …; await task;</c>) has no caller in its continuation chain.
	/// Wait for its awaiter to observe completion, as for a state machine's task
	/// </summary>
	private async Task<StepState?> TryWaitForChainsTask(ICorDebugThread thread, ICorDebugILFrame frame)
	{
		if (ChainOf(thread, frame) is not { } task) return null;
		try
		{
			if (await SetNotificationForWaitCompletion(task, thread) && TryCreateNotifyDebuggerOfWaitCompletionBreakpoint() is { } notifyBreakpoint)
			{
				_debugger.Log($"Step out of a resumed run that ends its chain on thread {thread.Id}: the step waits for its task's awaiter");
				return new StepState.AwaitingTaskCompletion(notifyBreakpoint);
			}
			return null;
		}
		finally
		{
			task.TryDispose();
		}
	}

	/// <summary>
	/// The runtime's dispatcher calls GetResult before resuming the awaiting method, so stepping out of GetResult cannot reach that
	/// caller. Wait at its resume offset instead. Null when the awaiting caller is already on the stack
	/// </summary>
	public StepState? TryWaitForResumingCaller(ICorDebugThread thread)
	{
		try
		{
			var top = thread.ActiveFrame;
			foreach (var frame in ManagedDebugger.EnumerateFramesForThread(thread))
			{
				if (frame is ICorDebugILFrame il && _modules.TryGetValue(il.Function.Module.BaseAddress, out var module) && module.IsUserCode && module.MetadataReader.HasSymbols) return null;
				if (IsDispatcher(frame) is false) continue;
				if (WaitingCallerOf(thread, top) is not { } waiting || IsRuntimeAsync(waiting) is false || AwaitIn(waiting) is not { } waited
				    || ResumeOf(waiting, waited) is not { } resumes || resumes < 0) return null;
				var chain = Dispatching(top, "CurrentTask")?.Dereference() is ICorDebugHeapValue2 task ? task.CreateHandle(CorDebugHandleType.HANDLE_STRONG) : null;
				// Exclude the awaiting caller itself from its waiter count
				return Wait(thread, StepType.StepOut, StepType.StepOut, top, waiting, waited, resumes, frameAwaits: false, ContinuationsAfter(thread, frame) - 1, chain);
			}
			return null;
		}
		catch (Exception ex)
		{
			_debugger.Log($"The runtime-async caller waiting for the task on thread {thread.Id} could not be read: {ex.Message}");
			return null;
		}
	}

	/// <summary>
	/// Keep a stepper until the frame leaves, so a synchronous completion or a step into a call still behaves as an ordinary step
	/// </summary>
	/// <param name="userType">The step the user asked for, which the stepper takes</param>
	/// <param name="type">May be StepOut even for a user step over, when the method has no statement left</param>
	private StepState.RuntimeAsyncAwait? Wait(ICorDebugThread thread, StepType userType, StepType type, ICorDebugFrame frame, ICorDebugILFrame awaiting,
		ModuleMetadataReader.RuntimeAwait awaited, int resume, bool frameAwaits, int? waiters, ICorDebugHandleValue? chain = null)
	{
		var function = awaiting.Function;
		chain ??= ChainOf(thread, frame);
		var past = StepRangeOf(awaiting, awaited.Call) is var (start, end) && (resume < start || resume >= end);
		var resumeBreakpoint = function.ILCode.CreateBreakpoint(resume);
		resumeBreakpoint.Activate(true);
		var step = new RuntimeAsyncStep(thread.Id, type, frame.StackRange, frame.Function.Module.BaseAddress, (int)frame.Function.Token, frameAwaits, resumeBreakpoint, past, chain, waiters)
		{
			// Waiters alone do not tell the run from another of the method started alongside it, e.g. passed to Task.WhenAll
			SuspendBreakpoint = frameAwaits && chain is null ? TryCreateChainSuspendBreakpoint(frame) : null
		};

		// With Just My Code, the stepper may keep running through the runtime after the frame leaves and skip the resume breakpoint
		// if the method resumes on that thread. Disable it so we observe the frame leaving
		var stepper = frameAwaits ? _debugger.CreateStepper(thread, userType, justMyCode: type is StepType.StepOver ? false : null) : null;
		_debugger.Log($"{type} through the runtime-async await at IL_{awaited.Call:X4} of method 0x{(int)function.Token:X} on thread {thread.Id}: "
			+ $"its run goes on at IL_{resume:X4}{(past ? ", past the await's statement" : "")}{(frameAwaits ? "" : ", its caller waiting in a continuation")}"
			+ (chain is null ? step.SuspendBreakpoint is null ? "" : ", its chain told once it suspends" : ", its chain's task told") + (waiters is { } count ? $", {count} waiting for it" : ""));
		return new StepState.RuntimeAsyncAwait(stepper, step);
	}

	/// <summary>
	/// Once the frame leaves at an await, its thread may run unrelated code: discard the stepper and wait for the resume breakpoint.
	/// A step out also waits once it reaches the awaiting caller. Null when the step should continue as a plain step
	/// </summary>
	public StepState.RuntimeAsyncAwait? WaitAtAwait(ICorDebugThread thread, StepState.RuntimeAsyncAwait state)
	{
		var step = state.Step;
		if (step.FrameAwaits is false || thread.Id != step.ThreadId || step.Left) return null;
		var held = ManagedDebugger.EnumerateFramesForThread(thread).Any(step.IsFrame);
		if (step.Type is StepType.StepOut ? step.IsFrame(thread.ActiveFrame) : held is false)
		{
			_debugger.Log(held ? "The step out is back in the awaiting caller: the step waits where it goes on after the await"
				: $"Thread {thread.Id} leaves the method at the await: the step waits where its run goes on");
			return new StepState.RuntimeAsyncAwait(null, step with { Left = held is false });
		}
		return null;
	}

	/// <summary>The breakpoint is shared by all activations, including those whose await completed synchronously</summary>
	public RuntimeResume TakeRuntimeResume(ICorDebugThread thread, RuntimeAsyncStep step)
	{
		var sameFrame = step is { FrameAwaits: true, Left: false } && thread.Id == step.ThreadId && step.IsFrame(thread.ActiveFrame);
		if (sameFrame) return step.Type is StepType.StepOut ? RuntimeResume.GoesOn : RuntimeResume.DoneAtOnce;
		if ((step.Left is false && Holds(step)) || IsStepsRun(step, thread) is false)
		{
			_debugger.Log($"Another run of the method goes on after the await on thread {thread.Id}: it runs on");
			return RuntimeResume.Another;
		}
		return RuntimeResume.GoesOn;
	}

	public enum RuntimeResume
	{
		/// <summary>Another run of the method, which the step lets run on</summary>
		Another,
		/// <summary>What the awaiting frame awaited was done at once: a step over or into goes on as it was</summary>
		DoneAtOnce,
		/// <summary>The stepping activation resumed, or a step out reached its caller after the await</summary>
		GoesOn
	}

	/// <summary>An exception on the stepping thread is caught: whether that unwinds the step's frame, so its await will never go on</summary>
	public bool UnwindsStep(ICorDebugThread thread, RuntimeAsyncStep step, ICorDebugFrame handler)
	{
		if (step.Left || thread.Id != step.ThreadId) return false;
		var frames = ManagedDebugger.EnumerateFramesForThread(thread).ToList();
		var stepping = frames.FindIndex(step.IsFrame);
		var caught = handler.StackRange;
		return stepping >= 0 && frames.FindIndex(frame => frame.StackRange == caught) > stepping;
	}

	/// <summary>The chain distinguishes concurrent calls; the waiter count distinguishes recursive calls within that chain</summary>
	private bool IsStepsRun(RuntimeAsyncStep step, ICorDebugThread thread)
	{
		try
		{
			var frame = thread.ActiveFrame;
			if (step.Chain is { } chain && Dispatching(frame, "CurrentTask") is { } running && running.Value != chain.Value) return false;
			if (step.Waiters is { } waiters && Waiters(thread, frame) is { } now && now != waiters) return false;
			return true;
		}
		catch (Exception ex)
		{
			_debugger.Log($"Which run goes on on thread {thread.Id} could not be told: {ex.Message}");
			return true;
		}
	}

	/// <summary>Whether the stepping thread still holds the step's frame; false for a thread that has ended</summary>
	private bool Holds(RuntimeAsyncStep step)
	{
		try
		{
			return _debugger.TryGetThread(step.ThreadId) is { } thread && ManagedDebugger.EnumerateFramesForThread(thread).Any(step.IsFrame);
		}
		catch (Exception)
		{
			return false;
		}
	}

	public bool HasRuntimeAwaitAhead(ICorDebugILFrame frame) => AwaitIn(frame) is not null;

	/// <summary>The await a runtime-async frame's statement has yet to go through</summary>
	private ModuleMetadataReader.RuntimeAwait? AwaitIn(ICorDebugILFrame frame)
	{
		if (MetadataReaderOf(frame) is not { } reader || StepRangeOf(frame, frame.IP.pnOffset) is not var (_, end)) return null;
		var ip = frame.IP.pnOffset;
		foreach (var awaited in reader.RuntimeAwaits((int)frame.Function.Token))
		{
			if (awaited.After > ip && awaited.Call < end) return awaited;
		}
		return null;
	}

	private ModuleMetadataReader.RuntimeAwait? AwaitOfCall(ICorDebugILFrame frame) =>
		MetadataReaderOf(frame)?.AwaitOfCallAt((int)frame.Function.Token, frame.IP.pnOffset);

	private bool LeavesMethod(ICorDebugILFrame frame) =>
		MetadataReaderOf(frame) is { } reader && StepRangeOf(frame, frame.IP.pnOffset) is var (_, end)
		&& reader.HasNoVisibleSequencePointFrom((int)frame.Function.Token, end);

	private bool IsRuntimeAsync(ICorDebugILFrame frame) => MetadataReaderOf(frame)?.IsRuntimeAsync((int)frame.Function.Token) is true;

	/// <summary>
	/// The task-returning thunk shares the method's token, so the token alone cannot identify the method's own code.
	/// The thunk has none of the method's IL mapped at its IP
	/// </summary>
	private bool IsRuntimeAsyncCode(ICorDebugILFrame frame) =>
		IsRuntimeAsync(frame) && frame.IP.pMappingResult is not (CorDebugMappingResult.MAPPING_UNMAPPED_ADDRESS or CorDebugMappingResult.MAPPING_NO_INFO);

	private ModuleMetadataReader? MetadataReaderOf(ICorDebugFrame frame) =>
		_modules.TryGetValue(frame.Function.Module.BaseAddress, out var module) && module.MetadataReader.HasSymbols ? module.MetadataReader : null;

	private (int Start, int End)? StepRangeOf(ICorDebugILFrame frame, int ilOffset)
	{
		if (MetadataReaderOf(frame)?.GetStartAndEndSequencePointIlOffsetsForIlOffset((int)frame.Function.Token, ilOffset) is not var (start, end)) return null;
		return (start, start == end ? frame.Function.ILCode.Size : end);
	}

	/// <summary>
	/// A resumed method's caller is no longer on the physical stack; it is the dispatcher's next continuation, if the chain has one
	/// </summary>
	private ICorDebugILFrame? WaitingCallerOf(ICorDebugThread thread, ICorDebugFrame frame)
	{
		try
		{
			return thread.Process is ICorDebugProcess12 asyncStacks && Dispatching(frame, "NextContinuation") is { } continuation
				&& asyncStacks.TryGetAsyncStack(continuation.Value, out var walk) is Cor.S_OK && walk is not null
				&& walk.TryGetFrame(out var waiting) is Cor.S_OK
				? waiting as ICorDebugILFrame
				: null;
		}
		catch (Exception ex)
		{
			_debugger.Log($"The continuation waiting for the run on thread {thread.Id} could not be read: {ex.Message}");
			return null;
		}
	}

	/// <summary>
	/// Count both physical callers and suspended continuations so the count stays the same across suspension.
	/// This distinguishes recursive activations in the same chain. Null when the count cannot be read
	/// </summary>
	private int? Waiters(ICorDebugThread thread, ICorDebugFrame frame)
	{
		try
		{
			var range = frame.StackRange;
			var count = 0;
			foreach (var below in ManagedDebugger.EnumerateFramesForThread(thread).SkipWhile(each => each.StackRange != range).Skip(1))
			{
				if (below is ICorDebugILFrame il && IsRuntimeAsyncCode(il) && AwaitOfCall(il) is not null)
				{
					count++;
					continue;
				}
				if (IsDispatcher(below)) return count + ContinuationsAfter(thread, below);
				return count;
			}
			return count;
		}
		catch (Exception ex)
		{
			_debugger.Log($"The runs waiting on thread {thread.Id} could not be counted: {ex.Message}");
			return null;
		}
	}

	private int ContinuationsAfter(ICorDebugThread thread, ICorDebugFrame frame)
	{
		if (thread.Process is not ICorDebugProcess12 asyncStacks || Dispatching(frame, "NextContinuation") is not { } continuation) return 0;
		if (asyncStacks.TryGetAsyncStack(continuation.Value, out var walk) is not Cor.S_OK || walk is null) throw new InvalidOperationException("The async stack could not be read");
		var count = 0;
		while (count < 10_000 && walk.TryGetFrame(out var each) is Cor.S_OK && each is not null)
		{
			count++;
			if (walk.TryNext() is not Cor.S_OK) break;
		}
		return count;
	}

	/// <summary>
	/// Only directly awaiting runtime-async callers share the dispatcher's chain. Any other caller starts a separate chain,
	/// whose task cannot be read from that dispatcher
	/// </summary>
	private ICorDebugHandleValue? ChainOf(ICorDebugThread thread, ICorDebugFrame frame)
	{
		try
		{
			var range = frame.StackRange;
			foreach (var below in ManagedDebugger.EnumerateFramesForThread(thread).SkipWhile(each => each.StackRange != range).Skip(1))
			{
				if (below is ICorDebugILFrame il && IsRuntimeAsyncCode(il))
				{
					if (AwaitOfCall(il) is null) return null;
					continue;
				}
				return IsDispatcher(below) && Dispatching(below, "CurrentTask")?.Dereference() is ICorDebugHeapValue2 task
					? task.CreateHandle(CorDebugHandleType.HANDLE_STRONG)
					: null;
			}
			return null;
		}
		catch (Exception ex)
		{
			_debugger.Log($"Which chain the run on thread {thread.Id} is in could not be read: {ex.Message}");
			return null;
		}
	}

	private RuntimeAsyncDispatchers Dispatchers => _dispatchers ??= new RuntimeAsyncDispatchers(_modules);
	private RuntimeAsyncDispatchers? _dispatchers;

	private bool IsDispatcher(ICorDebugFrame frame) => Dispatchers.IsDispatcher(frame);

	private ICorDebugReferenceValue? Dispatching(ICorDebugFrame frame, string field) => Dispatchers.Dispatching(frame, field);

	private int? ResumeOf(ICorDebugILFrame frame, ModuleMetadataReader.RuntimeAwait awaited) =>
		MetadataReaderOf(frame) is { } reader && MappedOffsets(frame) is { } mapped
			? reader.ResumeAfter((int)frame.Function.Token, awaited, mapped)
			: null;

	/// <summary>Breakpoints need mapped IL offsets; negative entries describe the prolog, epilog or code with no IL</summary>
	internal static HashSet<int>? MappedOffsets(ICorDebugILFrame frame)
	{
		ICorDebugCode? code = null;
		if ((frame is not ICorDebugNativeFrame native || native.TryGetCode(out code) is not Cor.S_OK) && frame.Function.TryGetNativeCode(out code) is not Cor.S_OK) return null;
		if (code is null || code.TryGetILToNativeMapping(0, out var count, null) is not Cor.S_OK || count == 0) return null;
		var map = new COR_DEBUG_IL_TO_NATIVE_MAP[count];
		return code.TryGetILToNativeMapping(count, out count, map) is Cor.S_OK
			? [.. map.Take((int)count).Select(entry => (int)entry.ilOffset).Where(offset => offset >= 0)]
			: null;
	}
}
