using ICorDebugSharp;

namespace SharpDbg.Infrastructure.Debugger;

/// <summary>
/// Reads the runtime's dispatchers of runtime-async continuations (.NET 11): AsyncHelpers.RuntimeAsyncTask's DispatchContinuations, which
/// calls each runtime-async method again where it goes on after an await that suspended. While one runs on a thread, the thread's
/// AsyncDispatcherInfo.t_current points at what it is doing, and its Next at what the dispatcher below it on the stack is doing
/// </summary>
internal sealed class RuntimeAsyncDispatchers(Dictionary<CORDB_ADDRESS, ModuleInfo> modules)
{
	private const string CoreLibName = "System.Private.CoreLib.dll";

	/// <summary>Whether a frame is the runtime's dispatcher of a chain of continuations</summary>
	public bool IsDispatcher(ICorDebugFrame frame)
	{
		if (frame.TryGetFunction(out var function) is not Cor.S_OK || function is null) return false;
		if (CoreLib() is not { } coreLib || function.Module.BaseAddress != coreLib.BaseAddress) return false;
		return function.Module.GetMetaDataInterface<IMetaDataImport>().GetMethodProps(function.Token).szMethod is "DispatchContinuations" or "InstrumentedDispatchContinuations";
	}

	/// <summary>
	/// A field of what the thread's innermost dispatcher is doing, read with a frame of the thread: the task it runs (CurrentTask), or the
	/// continuation it goes on with next (NextContinuation). None with no dispatcher on the thread, or a runtime with none
	/// </summary>
	public ICorDebugReferenceValue? Dispatching(ICorDebugFrame frame, string field) => Dispatchers(frame).FirstOrDefault() is { } innermost ? Field(innermost, field) : null;

	/// <summary>
	/// What each dispatcher on a frame's thread is doing, innermost (the one nearest the top of the stack) first, as the runtime chains them
	/// through AsyncDispatcherInfo.Next: one for each dispatcher frame on the stack, in the same order
	/// </summary>
	public IEnumerable<ICorDebugObjectValue> Dispatchers(ICorDebugFrame frame)
	{
		if (InfoType() is not var (info, current, _, _) || info.TryGetStaticFieldValue(current, frame, out var pointer) is not Cor.S_OK) yield break;
		for (var depth = 0; depth < 1_000 && pointer is ICorDebugReferenceValue { IsNull: false } reference && reference.Dereference() is ICorDebugObjectValue dispatching; depth++)
		{
			yield return dispatching;
			pointer = Field(dispatching, "Next");
		}
	}

	/// <summary>
	/// The frames of the runtime-async methods waiting, in turn, on what a dispatcher runs: the continuations it goes on with after the one it
	/// runs, read with ICorDebugProcess12.GetAsyncStack. They live only until the process next runs
	/// </summary>
	public IReadOnlyList<ICorDebugILFrame> WaitingFrames(ICorDebugProcess process, ICorDebugObjectValue dispatching)
	{
		if (process is not ICorDebugProcess12 asyncStacks || Field(dispatching, "NextContinuation") is not { } continuation) return [];
		if (asyncStacks.TryGetAsyncStack(continuation.Value, out var walk) is not Cor.S_OK || walk is null) return [];
		var frames = new List<ICorDebugILFrame>();
		while (frames.Count < 10_000 && walk.TryGetFrame(out var frame) is Cor.S_OK && frame is not null)
		{
			if (frame is ICorDebugILFrame ilFrame) frames.Add(ilFrame);
			if (walk.TryNext() is not Cor.S_OK) break;
		}
		return frames;
	}

	private ICorDebugReferenceValue? Field(ICorDebugObjectValue dispatching, string field)
	{
		if (InfoType() is not var (info, _, metadata, infoType)) return null;
		var wanted = metadata.EnumFieldsWithName(infoType, field).SingleOrDefault();
		return !wanted.IsNil && dispatching.GetFieldValue(info, wanted) is ICorDebugReferenceValue { IsNull: false } value ? value : null;
	}

	private (ICorDebugClass Info, mdFieldDef Current, IMetaDataImport Metadata, mdTypeDef InfoType)? InfoType()
	{
		if (CoreLib() is not { } coreLib) return null;
		var metadata = coreLib.GetMetaDataInterface<IMetaDataImport>();
		if (metadata.FindTypeDefByNameOrNull("System.Runtime.CompilerServices.AsyncDispatcherInfo", mdToken.Nil) is not { } infoType) return null;
		var current = metadata.EnumFieldsWithName(infoType, "t_current").SingleOrDefault();
		return current.IsNil ? null : (coreLib.GetClassFromToken(infoType), current, metadata, infoType);
	}

	private ICorDebugModule? CoreLib() =>
		modules.Values.Select(module => module.Module).FirstOrDefault(module => module.Name.EndsWith(CoreLibName, StringComparison.OrdinalIgnoreCase));
}
