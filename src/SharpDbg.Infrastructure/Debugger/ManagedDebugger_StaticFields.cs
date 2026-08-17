using System.Runtime.InteropServices;
using ICorDebugSharp;

namespace SharpDbg.Infrastructure.Debugger;

public partial class ManagedDebugger
{
	internal async ValueTask<ICorDebugValue> GetStaticFieldValueAsync(ICorDebugType type, mdFieldDef fieldDef, ThreadId threadId, FrameStackDepth stackDepth)
	{
		await EnsureClassConstructorHasRun(type, threadId, stackDepth);

		var currentFrame = GetIlFrameForThreadIdAndStackDepth(threadId, stackDepth);
		var result = type.TryGetStaticFieldValue(fieldDef, currentFrame, out var value);
		Marshal.ThrowExceptionForHR(result);
		return value;
	}

	private async ValueTask EnsureClassConstructorHasRun(ICorDebugType type, ThreadId threadId, FrameStackDepth stackDepth)
	{
		var hasTypeId = type.TryGetTypeID(out var typeId) is Cor.S_OK; // GetTypeID can/will throw for generic static types
		if (hasTypeId && _initializedStaticTypes.Contains(typeId)) return;

		var suppressFinalize = GetSuppressFinalizeFunction();
		var typeParameters = type.TypeParameters;
		var thread = GetIlFrameForThreadIdAndStackDepth(threadId, stackDepth).Chain.Thread;
		var eval = thread.CreateEval();
		var initializationValue = await eval.NewParameterizedObjectNoConstructorAsync(ProcessRuntimeEventsUntilEvalEvent, EvalStatus, type.Class, typeParameters.Length, typeParameters, throwOnException: true) ?? throw new EvalException("Type initialization returned no value");

		try
		{
			var suppressEval = thread.CreateEval();
			var suppressResult = await suppressEval.CallParameterizedFunctionAsync(ProcessRuntimeEventsUntilEvalEvent, EvalStatus, suppressFinalize, 0, null, 1, [initializationValue], throwOnException: true);
			if (suppressResult is ICorDebugHandleValue suppressHandle) suppressHandle.TryDispose();
			if (hasTypeId || type.TryGetTypeID(out typeId) is Cor.S_OK) _initializedStaticTypes.Add(typeId);
		}
		finally
		{
			if (initializationValue is ICorDebugHandleValue initializationHandle) initializationHandle.TryDispose();
		}
	}

	private ICorDebugFunction GetSuppressFinalizeFunction()
	{
		if (_suppressFinalizeFunction is not null) return _suppressFinalizeFunction;

		var coreLib = _modules.Values.SingleOrDefault(module => module.ModuleName == "System.Private.CoreLib.dll") ?? throw new InvalidOperationException("System.Private.CoreLib.dll is not loaded");
		var metadata = coreLib.Module.GetMetaDataInterface<IMetaDataImport>();
		var gcType = metadata.FindTypeDefByNameOrNull("System.GC", mdToken.Nil) ?? throw new InvalidOperationException("Could not find System.GC");
		var suppressFinalize = metadata.EnumMethodsWithName(gcType, "SuppressFinalize").SingleOrDefault();
		if (suppressFinalize.IsNil) throw new InvalidOperationException("Could not find System.GC.SuppressFinalize");

		return _suppressFinalizeFunction = coreLib.Module.GetFunctionFromToken(suppressFinalize);
	}
}
