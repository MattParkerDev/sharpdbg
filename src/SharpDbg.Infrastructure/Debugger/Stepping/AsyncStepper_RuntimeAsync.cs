using ICorDebugSharp;

namespace SharpDbg.Infrastructure.Debugger.Stepping;

/// <summary>
/// Steps through the awaits of runtime-async methods (.NET 11), which have no state machine. Ported from Passenger's AsyncStepper_RuntimeAsync
/// </summary>
public partial class AsyncStepper
{
	/// <summary>
	/// The state to start a step through an await of a runtime-async method with: over or into the frame's statement when it awaits, or out
	/// of the frame's method (a step out, a step past its end, or over an await it returns right after) to the await its caller waits at.
	/// Null when the step goes through no such await
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
	/// A resumed run that ends its chain, whose task its caller awaits apart from the call (<c>var task = WorkAsync(); …; await task;</c>):
	/// wait for that task's awaiter to see it complete, as for a state machine's task. None for a run not under the runtime's dispatcher,
	/// which has its caller on the stack
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
	/// A task's awaiter has seen it complete (Task.NotifyDebuggerOfWaitCompletion), for a step out waiting for it: when the awaiter is
	/// the runtime's dispatcher, about to call the runtime-async method that awaits the task again, wait where that method goes on. Null
	/// when user code awaits the task on the stack, as a state machine does
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
				// The continuations after the one the dispatcher goes on with are what wait for the awaiting caller
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
	/// Arms the step's breakpoint where the awaiting frame's method goes on after its await, and, while the stepping frame awaits, the stepper
	/// that runs until it leaves there
	/// </summary>
	/// <param name="userType">The step the user asked for, which the stepper takes</param>
	/// <param name="type">The step the step is: a step out of the frame to its caller's await, or over or into the frame's own</param>
	private StepState.RuntimeAsyncAwait? Wait(ICorDebugThread thread, StepType userType, StepType type, ICorDebugFrame frame, ICorDebugILFrame awaiting,
		ModuleMetadataReader.RuntimeAwait awaited, int resume, bool frameAwaits, int? waiters, ICorDebugHandleValue? chain = null)
	{
		var function = awaiting.Function;
		chain ??= ChainOf(thread, frame);
		var past = StepRangeOf(awaiting, awaited.Call) is var (start, end) && (resume < start || resume >= end);
		var resumeBreakpoint = function.ILCode.CreateBreakpoint(resume);
		resumeBreakpoint.Activate(true);
		var step = new RuntimeAsyncStep(thread.Id, type, frame.StackRange, frame.Function.Module.BaseAddress, (int)frame.Function.Token, frameAwaits, resumeBreakpoint, past, chain, waiters);

		// Stepping over an await that suspends, the step sees the frame leave wherever it returns to, Just My Code or not: Just My Code, the
		// stepper would run on through the code that called it, and, should that thread call the method again where it goes on, pass the
		// step's breakpoint by
		var stepper = frameAwaits ? _debugger.CreateStepper(thread, userType, justMyCode: type is StepType.StepOver ? false : null) : null;
		_debugger.Log($"{type} through the runtime-async await at IL_{awaited.Call:X4} of method 0x{(int)function.Token:X} on thread {thread.Id}: "
			+ $"its run goes on at IL_{resume:X4}{(past ? ", past the await's statement" : "")}{(frameAwaits ? "" : ", its caller waiting in a continuation")}"
			+ (chain is null ? "" : ", its chain's task told") + (waiters is { } count ? $", {count} waiting for it" : ""));
		return new StepState.RuntimeAsyncAwait(stepper, step);
	}

	/// <summary>
	/// The step's stepper has stopped on the stepping thread: the state the step goes on with, waiting where its run goes on, when the
	/// awaiting frame has left at its await (or, for a step out, the awaiting caller has been reached). Null when the step ends as a plain one
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
		// Stopped in the awaiting frame, or in a call it made: the await is not the step's to wait for
		return null;
	}

	/// <summary>Whether the resume breakpoint's hit on a thread is the step's own run going on after the await</summary>
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
		/// <summary>The step's run goes on after the await: the step goes on from there</summary>
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

	/// <summary>Whether the step's run goes on at the active frame, telling it apart from other runs of its method by its chain and its waiters</summary>
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

	/// <summary>Whether a runtime-async frame's statement has an await yet to go through</summary>
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

	/// <summary>The await of the call a runtime-async frame is in, which its callee's run returns to</summary>
	private ModuleMetadataReader.RuntimeAwait? AwaitOfCall(ICorDebugILFrame frame) =>
		MetadataReaderOf(frame)?.AwaitOfCallAt((int)frame.Function.Token, frame.IP.pnOffset);

	/// <summary>Whether a step from a frame's statement leaves its method: no line of it comes after the statement</summary>
	private bool LeavesMethod(ICorDebugILFrame frame) =>
		MetadataReaderOf(frame) is { } reader && StepRangeOf(frame, frame.IP.pnOffset) is var (_, end)
		&& reader.HasNoVisibleSequencePointFrom((int)frame.Function.Token, end);

	private bool IsRuntimeAsync(ICorDebugILFrame frame) => MetadataReaderOf(frame)?.IsRuntimeAsync((int)frame.Function.Token) is true;

	/// <summary>
	/// Whether a frame runs a runtime-async method's own code: not its task-returning thunk, which shares the method's token, with none of
	/// the method's IL mapped at its IP
	/// </summary>
	private bool IsRuntimeAsyncCode(ICorDebugILFrame frame) =>
		IsRuntimeAsync(frame) && frame.IP.pMappingResult is not (CorDebugMappingResult.MAPPING_UNMAPPED_ADDRESS or CorDebugMappingResult.MAPPING_NO_INFO);

	private ModuleMetadataReader? MetadataReaderOf(ICorDebugFrame frame) =>
		_modules.TryGetValue(frame.Function.Module.BaseAddress, out var module) && module.MetadataReader.HasSymbols ? module.MetadataReader : null;

	/// <summary>The IL range of the statement at an IL offset, as a step over or into steps through it</summary>
	private (int Start, int End)? StepRangeOf(ICorDebugILFrame frame, int ilOffset)
	{
		if (MetadataReaderOf(frame)?.GetStartAndEndSequencePointIlOffsetsForIlOffset((int)frame.Function.Token, ilOffset) is not var (start, end)) return null;
		return (start, start == end ? frame.Function.ILCode.Size : end);
	}

	/// <summary>
	/// The frame of the method that waits for a resumed run to end: the first of the continuations the thread's dispatcher goes on with, as
	/// the runtime reads them. None for a run that ends its chain
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
	/// How many runs wait for a runtime-async frame's run: the runtime-async frames below it on the stack that await the call above them, and,
	/// below those, the continuations the dispatcher goes on with. Null when they could not be counted
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

	/// <summary>How many continuations the dispatcher on a thread goes on with after the one it runs</summary>
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
	/// The task whose chain of continuations a frame's run is in, by a strong handle: the one the dispatcher below it on the stack runs, when
	/// only runtime-async frames awaiting the call above them lie between. None below any other, which started the chain on this run
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

	/// <summary>Whether a frame is the runtime's dispatcher of a chain of continuations, which calls each method again where it goes on</summary>
	private bool IsDispatcher(ICorDebugFrame frame)
	{
		if (frame.TryGetFunction(out var function) is not Cor.S_OK || function is null) return false;
		if (CoreLib() is not { } coreLib || function.Module.BaseAddress != coreLib.BaseAddress) return false;
		return function.Module.GetMetaDataInterface<IMetaDataImport>().GetMethodProps(function.Token).szMethod is "DispatchContinuations" or "InstrumentedDispatchContinuations";
	}

	private ICorDebugModule? CoreLib() =>
		_modules.Values.Select(module => module.Module).FirstOrDefault(module => module.Name.EndsWith("System.Private.CoreLib.dll", StringComparison.OrdinalIgnoreCase));

	/// <summary>
	/// A field of what the thread's innermost dispatcher of continuations is doing (AsyncDispatcherInfo.t_current, read with a frame of the
	/// thread): the task it runs (CurrentTask), or the continuation it goes on with next (NextContinuation). None with no dispatcher on the
	/// thread, or a runtime with none
	/// </summary>
	private ICorDebugReferenceValue? Dispatching(ICorDebugFrame frame, string field)
	{
		if (CoreLib() is not { } coreLib) return null;
		var metadata = coreLib.GetMetaDataInterface<IMetaDataImport>();
		if (metadata.FindTypeDefByNameOrNull("System.Runtime.CompilerServices.AsyncDispatcherInfo", mdToken.Nil) is not { } infoType) return null;
		var current = metadata.EnumFieldsWithName(infoType, "t_current").SingleOrDefault();
		var wanted = metadata.EnumFieldsWithName(infoType, field).SingleOrDefault();
		var info = coreLib.GetClassFromToken(infoType);
		return !current.IsNil && !wanted.IsNil && info.TryGetStaticFieldValue(current, frame, out var pointer) is Cor.S_OK
			&& pointer is ICorDebugReferenceValue { IsNull: false } dispatcher && dispatcher.Dereference() is ICorDebugObjectValue dispatching
			&& dispatching.GetFieldValue(info, wanted) is ICorDebugReferenceValue { IsNull: false } value
				? value
				: null;
	}

	/// <summary>
	/// Where a frame's method goes on after an await (<see cref="ModuleMetadataReader.ResumeAfter"/>): at once when what it awaited was done,
	/// else once the runtime calls it again there
	/// </summary>
	private int? ResumeOf(ICorDebugILFrame frame, ModuleMetadataReader.RuntimeAwait awaited) =>
		MetadataReaderOf(frame) is { } reader && MappedOffsets(frame) is { } mapped
			? reader.ResumeAfter((int)frame.Function.Token, awaited, mapped)
			: null;

	/// <summary>The IL offsets a frame's compiled code maps, where a breakpoint binds; the prolog's, the epilog's and code of no IL left out</summary>
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
