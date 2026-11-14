using ICorDebugSharp;

namespace SharpDbg.Infrastructure.Debugger.Stepping;

/// <summary>
/// A new runtime-async chain has no task until it first suspends. The thunk then calls NotifyDebuggerOfRuntimeAsyncState,
/// with async debugging enabled, before returning to its caller. This lets us capture the chain's task after the stepping frame
/// leaves, before unrelated work can run on that thread
/// </summary>
public partial class AsyncStepper
{
	private const string CoreLibName = "System.Private.CoreLib.dll";

	// Async debugging stays enabled, so it only needs to be set once
	private bool _asyncDebuggingEnabled;

	/// <summary>
	/// Capture the task when the chain first suspends, so concurrent calls can be told apart at the resume breakpoint.
	/// Null when the runtime hook is unavailable or async debugging cannot be enabled
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
	/// The runtime only calls the debugger hooks with async debugging enabled. The Synchronize bit makes the next instrumentation
	/// checkpoint reread Task.s_asyncDebuggingEnabled
	/// </summary>
	private bool EnableAsyncDebugging(ICorDebugModule coreLib, ICorDebugFrame frame)
	{
		if (_asyncDebuggingEnabled) return true;
		var metadata = coreLib.GetMetaDataInterface<IMetaDataImport>();
		if (StaticField(coreLib, metadata, "System.Threading.Tasks.Task", "s_asyncDebuggingEnabled", frame) is not { } enabled) return false;
		Write(enabled, [1]);

		// If AsyncInstrumentation is not initialized yet, its initial Synchronize bit will make it read the field anyway
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
	/// A nested call can suspend its own chain while the stepping frame is still on the stack. Only capture the task after that frame
	/// has left on the stepping thread; otherwise this notification belongs to another chain
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
