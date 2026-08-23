using ICorDebugSharp;

namespace SharpDbg.Infrastructure.Debugger;

// After a continuation, async methods are resumed on a different thread, so the caller of the async method is not visible in the physical stack trace
// This class provides a way to synthesize the caller frames of async methods by inspecting the state machine and its associated task continuation chain
internal sealed record SyntheticAsyncCallerFrame(
	ModuleInfo Module,
	int KickoffMethodToken,
	int MoveNextToken,
	ICorDebugValue StateMachine,
	string Name);

public partial class ManagedDebugger
{
	private const int SyntheticAsyncCallerDepthLimit = 100;
	private const int MaxStateMachineSearchDepth = 100;

	private IEnumerable<SyntheticAsyncCallerFrame> GetSyntheticAsyncCallerFrames(ICorDebugILFrame physicalFrame)
	{
		if (!_modules.TryGetValue(physicalFrame.Function.Module.BaseAddress, out var physicalModule) ||
			physicalModule.MetadataReader.GetStateMachineKickoffMethodToken(physicalFrame.Function.Token) is null)
			yield break;

		ICorDebugValue stateMachine;
		try
		{
			stateMachine = physicalFrame.GetArgument(0);
		}
		catch
		{
			yield break;
		}

		var visitedTasks = new HashSet<CORDB_ADDRESS>();
		var visitedStateMachines = new HashSet<CORDB_ADDRESS>();
		if (stateMachine is ICorDebugReferenceValue initialStateMachineReference) visitedStateMachines.Add(initialStateMachineReference.Value);

		for (var syntheticFrameIndex = 0; syntheticFrameIndex < SyntheticAsyncCallerDepthLimit; syntheticFrameIndex++)
		{
			var task = GetStateMachineTask(stateMachine);
			if (task is null) yield break;
			if (task is ICorDebugReferenceValue taskReference && !visitedTasks.Add(taskReference.Value)) yield break;
			var taskObject = task.UnwrapDebugValueToObjectOrNull();
			if (taskObject is null) yield break;
			var continuation = GetInstanceFieldValue(taskObject, "m_continuationObject");
			if (continuation is null) yield break;
			stateMachine = FindStateMachine(continuation, 0)!;
			if (stateMachine is null) yield break;

			// A repeated state machine means the continuation chain is cyclic, e.g. two async methods awaiting
			// each other's tasks. Stop instead of fabricating frames forever.
			if (stateMachine is ICorDebugReferenceValue stateMachineReference && !visitedStateMachines.Add(stateMachineReference.Value)) yield break;

			var frame = CreateSyntheticAsyncCallerFrame(stateMachine);
			if (frame is null) yield break;
			yield return frame;
		}
	}

	private static ICorDebugValue? GetStateMachineTask(ICorDebugValue stateMachine)
	{
		var stateMachineObject = stateMachine.UnwrapDebugValueToObjectOrNull();
		var builder = stateMachineObject is null ? null : GetInstanceFieldValue(stateMachineObject, "<>t__builder") ??
			GetInstanceFieldValue(stateMachineObject, "$Builder");
		var builderObject = builder is null ? null : builder.UnwrapDebugValueToObjectOrNull();
		return builderObject is null ? null : GetInstanceFieldValue(builderObject, "m_task") ??
			GetInstanceFieldValue(builderObject, "_task");
	}

	private static readonly string[] StateMachineFieldNameCandidates = ["StateMachine", "m_stateMachine", "m_action"];
	private static ICorDebugValue? FindStateMachine(ICorDebugValue value, int depth)
	{
		if (depth >= MaxStateMachineSearchDepth) return null;
		var objectValue = value.UnwrapDebugValueToObjectOrNull();
		if (objectValue is null) return null;

		foreach (var fieldName in StateMachineFieldNameCandidates)
		{
			var nested = GetInstanceFieldValue(objectValue, fieldName);
			if (nested is null) continue;
			var stateMachine = FindStateMachine(nested, depth + 1);
			if (stateMachine is not null) return stateMachine;
		}

		var metadata = objectValue.Class.Module.GetMetaDataInterface<IMetaDataImport>();
		if (metadata.EnumMethodsWithName(objectValue.Class.Token, "MoveNext").Any()) return value;

		if (objectValue is ICorDebugDelegateObjectValue delegateValue && delegateValue.Target.IsNull is false)
			return FindStateMachine(delegateValue.Target, depth + 1);
		return null;
	}

	private SyntheticAsyncCallerFrame? CreateSyntheticAsyncCallerFrame(ICorDebugValue stateMachine)
	{
		var objectValue = stateMachine.UnwrapDebugValueToObjectOrNull();
		if (objectValue is null || !_modules.TryGetValue(objectValue.Class.Module.BaseAddress, out var module)) return null;
		var metadata = objectValue.Class.Module.GetMetaDataInterface<IMetaDataImport>();
		var moveNext = metadata.EnumMethodsWithName(objectValue.Class.Token, "MoveNext").FirstOrDefault();
		if (moveNext.IsNil) return null;
		var moveNextToken = (int)moveNext;
		var kickoffToken = module.MetadataReader.GetStateMachineKickoffMethodToken(moveNextToken);
		if (kickoffToken is null) return null;

		var name = GetMethodFormattedName(module, kickoffToken.Value, objectValue.ExactType.TypeParameters);
		return new SyntheticAsyncCallerFrame(module, kickoffToken.Value, moveNextToken, stateMachine, name);
	}

	/// Source info for a synthetic frame is the location of the await the caller is currently suspended on. It is
	/// resolved on demand so that a stack trace request never has to generate PDBs; pass decompileIfNeeded when
	/// resolving an individual frame to decompile the module first if it has no symbols on disk.
	private SourceInfo? GetSyntheticAsyncFrameSourceInfo(SyntheticAsyncCallerFrame frame, ThreadId threadId, FrameStackDepth physicalFrameStackDepth, bool decompileIfNeeded)
	{
		var module = frame.Module;
		if (module.MetadataReader.HasSymbols is false && decompileIfNeeded)
		{
			// No PDB on disk — generate one via decompilation and update the module entry
			if (GetCachedOrGeneratePdb(module))
			{
				module.SymbolsFromDecompiled = true;
			}
		}
		if (module.MetadataReader.HasSymbols is false) return null;

		var objectValue = frame.StateMachine.UnwrapDebugValueToObjectOrNull();
		if (objectValue is null) return null;
		var state = ReadInt32Field(objectValue, "<>1__state") ?? ReadInt32Field(objectValue, "$State");
		var steppingInfo = module.MetadataReader.GetAsyncMethodSteppingInfo(frame.MoveNextToken);
		if (state is null || state < 0 || steppingInfo is null || state >= steppingInfo.AwaitInfos.Count) return null;

		var awaitInfo = steppingInfo.AwaitInfos[state.Value];
		var source = module.MetadataReader.GetSourceLocationForOffset(frame.MoveNextToken, checked((int)awaitInfo.YieldOffset));
		if (source is null) return null;

		DecompiledSourceInfo? decompiledSourceInfo = null;
		if (module.SymbolsFromDecompiled)
		{
			// Synthetic frames have no physical caller chain, so start the search from the physical frame the
			// synthetic chain was built from
			var originatingFrame = GetFrameForThreadIdAndStackDepth(threadId, physicalFrameStackDepth);
			var callingUserCodeAssemblyPath = FindCallingUserCodeAssemblyPath(originatingFrame);
			decompiledSourceInfo = CreateDecompiledSourceInfo(module, frame.MoveNextToken, callingUserCodeAssemblyPath);
		}

		return new SourceInfo(source.Value.sourceFilePath, source.Value.startLine, source.Value.endLine, source.Value.startColumn, source.Value.endColumn, decompiledSourceInfo);
	}

	private static int? ReadInt32Field(ICorDebugObjectValue objectValue, string name)
	{
		try
		{
			var field = GetInstanceFieldValue(objectValue, name)?.UnwrapDebugValue() as ICorDebugGenericValue;
			return field is null ? null : BitConverter.ToInt32(field.GetValueAsBytes());
		}
		catch
		{
			return null;
		}
	}

	private static ICorDebugValue? GetInstanceFieldValue(ICorDebugObjectValue objectValue, string fieldName)
	{
		for (var type = objectValue.ExactType; type is not null; type = type.Base)
		{
			var metadata = type.Class.Module.GetMetaDataInterface<IMetaDataImport>();
			var field = metadata.EnumFieldsWithName(type.Class.Token, fieldName).FirstOrDefault();
			if (!field.IsNil) return objectValue.GetFieldValue(type.Class, field);
		}
		return null;
	}
}
