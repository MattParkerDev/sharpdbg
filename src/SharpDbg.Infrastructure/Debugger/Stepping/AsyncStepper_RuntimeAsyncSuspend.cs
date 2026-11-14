using ICorDebugSharp;

namespace SharpDbg.Infrastructure.Debugger.Stepping;

/// <summary>
/// Tells which run a runtime-async step is in when the run's chain has not suspended yet, so has no task: a run its caller does not await,
/// or one under a caller that is not runtime-async. When such a chain first suspends, the task-returning thunk at its bottom gives it a task
/// (AsyncHelpers.CreateRuntimeAsyncTask), and, with async debugging on, calls the task's NotifyDebuggerOfRuntimeAsyncState for the debugger.
/// The step's frame has left by then, and nothing else has run on the thread: the task there is the run's, and its chain's
/// </summary>
public partial class AsyncStepper
{
	private const string CoreLibName = "System.Private.CoreLib.dll";

	/// <summary>Whether async debugging is on in the debuggee, which it stays once on</summary>
	private bool _asyncDebuggingEnabled;

	/// <summary>
	/// A breakpoint where a chain that has first suspended is given its task, for a step whose run's chain has none yet. Null when the
	/// runtime has no such hook, or async debugging could not be turned on for it to be called
	/// </summary>
	private ICorDebugFunctionBreakpoint? TryCreateChainSuspendBreakpoint(ICorDebugFrame frame)
	{
		try
		{
			if (CoreLibModule() is not { } coreLib || EnableAsyncDebugging(coreLib, frame) is false) return null;
			var metadata = coreLib.GetMetaDataInterface<IMetaDataImport>();
			if (metadata.FindTypeDefByNameOrNull("System.Runtime.CompilerServices.AsyncHelpers", mdToken.Nil) is not { } helpers
			    || metadata.FindTypeDefByNameOrNull("RuntimeAsyncTask`1", helpers) is not { } task) return null;
			var notify = metadata.FindMethod(task, "NotifyDebuggerOfRuntimeAsyncState", 0, 0);
			if (notify.IsNil) return null;
			// The method is not optimized, for its task to be read; the breakpoint binds to every instantiation of the generic task
			var breakpoint = coreLib.GetFunctionFromToken(notify).ILCode.CreateBreakpoint(0);
			breakpoint.Activate(true);
			return breakpoint;
		}
		catch (Exception ex)
		{
			_debugger.Log($"The hook where a runtime-async chain is given its task could not be set: {ex.Message}");
			return null;
		}
	}

	/// <summary>
	/// Turn async debugging on in the debuggee, for the runtime to call its debugger hooks: Task.s_asyncDebuggingEnabled, then the
	/// Synchronize bit of AsyncInstrumentation's active flags, for the next of its checkpoints to read it again
	/// </summary>
	private bool EnableAsyncDebugging(ICorDebugModule coreLib, ICorDebugFrame frame)
	{
		if (_asyncDebuggingEnabled) return true;
		var metadata = coreLib.GetMetaDataInterface<IMetaDataImport>();
		if (StaticField(coreLib, metadata, "System.Threading.Tasks.Task", "s_asyncDebuggingEnabled", frame) is not { } enabled) return false;
		Write(enabled, [1]);

		// Until AsyncInstrumentation is first used, its flags start with the Synchronize bit set, which reads the field anyway
		if (StaticField(coreLib, metadata, "System.Runtime.CompilerServices.AsyncInstrumentation", "s_activeFlags", frame) is { } flags)
		{
			const uint synchronize = 0x80000000;
			var current = BitConverter.ToUInt32(flags.GetValueAsBytes());
			Write(flags, BitConverter.GetBytes(current | synchronize));
		}
		_debugger.Log("Async debugging is on, for the runtime to tell which run a runtime-async step is in when its chain suspends");
		return _asyncDebuggingEnabled = true;
	}

	private static ICorDebugGenericValue? StaticField(ICorDebugModule module, IMetaDataImport metadata, string typeName, string fieldName, ICorDebugFrame frame)
	{
		if (metadata.FindTypeDefByNameOrNull(typeName, mdToken.Nil) is not { } type) return null;
		var field = metadata.EnumFieldsWithName(type, fieldName).SingleOrDefault();
		if (field.IsNil) return null;
		return module.GetClassFromToken(type).TryGetStaticFieldValue(field, frame, out var value) is Cor.S_OK ? value as ICorDebugGenericValue : null;
	}

	private static unsafe void Write(ICorDebugGenericValue value, byte[] bytes)
	{
		fixed (byte* pointer = bytes) value.SetValue((nint)pointer);
	}

	/// <summary>
	/// A chain has first suspended on a thread and been given its task (the chain-suspend breakpoint): when it is the step's run's, its frame
	/// having left at the await, the state the step goes on with, waiting for that chain's task to go on. Null when it is another chain, one
	/// that a call in the step's statement started, with the step's frame still on the stack
	/// </summary>
	public StepState.RuntimeAsyncAwait? TakeSuspendedChain(ICorDebugThread thread, StepState.RuntimeAsyncAwait state)
	{
		var step = state.Step;
		if (thread.Id != step.ThreadId || step.Left || step.Chain is not null) return null;
		try
		{
			if (ManagedDebugger.EnumerateFramesForThread(thread).Any(step.IsFrame)) return null;
			if (thread.ActiveFrame is not ICorDebugILFrame notify || notify.TryGetArgument(0, out var self) is not Cor.S_OK
			    || self is not ICorDebugReferenceValue { IsNull: false } reference || reference.Dereference() is not ICorDebugHeapValue2 task) return null;
			_debugger.Log($"Thread {thread.Id} leaves the method at the await, and its run's chain suspends: the step waits where that chain's run goes on");
			return new StepState.RuntimeAsyncAwait(null, step with { Left = true, Chain = task.CreateHandle(CorDebugHandleType.HANDLE_STRONG), SuspendBreakpoint = null });
		}
		catch (Exception ex)
		{
			_debugger.Log($"The task of the chain suspending on thread {thread.Id} could not be read: {ex.Message}");
			return null;
		}
	}

	private ICorDebugModule? CoreLibModule() =>
		_modules.Values.Select(module => module.Module).FirstOrDefault(module => module.Name.EndsWith(CoreLibName, StringComparison.OrdinalIgnoreCase));
}
