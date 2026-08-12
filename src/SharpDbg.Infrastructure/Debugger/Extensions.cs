using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ICorDebugSharp;

namespace SharpDbg.Infrastructure.Debugger;

public static class Extensions
{
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool IsLiteral(this mdFieldDef mdFieldDef, IMetaDataImport metadataImport)
	{
		var fieldProps = metadataImport.GetFieldProps(mdFieldDef);
		var isStatic = fieldProps.pdwAttr.IsFdLiteral();
		return isStatic;
	}
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool IsStatic(this mdFieldDef mdFieldDef, IMetaDataImport metadataImport)
	{
		var fieldProps = metadataImport.GetFieldProps(mdFieldDef);
		var isStatic = fieldProps.pdwAttr.IsFdStatic();
		return isStatic;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool IsPublic(this mdFieldDef mdFieldDef, IMetaDataImport metadataImport)
	{
		var fieldProps = metadataImport.GetFieldProps(mdFieldDef);
		var isPublic = fieldProps.pdwAttr.IsFdPublic();
		return isPublic;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool IsPublic(this mdProperty mdProperty, IMetaDataImport metadataImport)
	{
		var propertyProps = metadataImport.GetPropertyProps(mdProperty);
		var getterMethodProps = metadataImport.GetMethodProps(propertyProps.pmdGetter);
		var isPublic = getterMethodProps.pdwAttr.IsMdPublic();
		return isPublic;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool IsStatic(this mdProperty mdProperty, IMetaDataImport metadataImport)
	{
		var propertyProps = metadataImport.GetPropertyProps(mdProperty);
		var getterMethodProps = metadataImport.GetMethodProps(propertyProps.pmdGetter);
		var isStatic = getterMethodProps.pdwAttr.IsMdStatic();
		return isStatic;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool IsStatic(this mdMethodDef methodToken, IMetaDataImport metaDataImport)
	{
		var methodProps = metaDataImport.GetMethodProps(methodToken);
		var isStatic = methodProps.pdwAttr.IsMdStatic();
		return isStatic;
	}

	// To my knowledge, only strings from CustomAttributes 'Type' ctor use this '+' format
	public static mdTypeDef? FindMaybeNestedTypeDefByNameOrNull(this IMetaDataImport metadataImport, string typeName)
	{
		var nestedClasses = typeName.Split('+');
		mdTypeDef? enclosingClass = null;
		foreach (var nestedClass in nestedClasses)
		{
			var mdTypeDef = metadataImport.FindTypeDefByNameOrNull(nestedClass, enclosingClass ?? mdToken.Nil);
			if (mdTypeDef is null) return null;
			enclosingClass = mdTypeDef;
		}
		return enclosingClass;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static mdTypeDef? FindTypeDefByNameOrNull(this IMetaDataImport metadataImport, string typeName, mdToken enclosingClass)
	{
		var result = metadataImport.TryFindTypeDefByName(typeName, enclosingClass, out var mdTypeDef);
		if (result is Cor.S_OK) return mdTypeDef;
		return null;
	}

	public static mdTypeDef? FindTypeDefByNameOrNullInCandidateNamespaces(this IMetaDataImport metadataImport, string typeName, mdToken enclosingClass, ImmutableArray<string> candidateNamespaces)
	{
		foreach (var candidateNamespace in candidateNamespaces)
		{
			var fullTypeName = string.IsNullOrEmpty(candidateNamespace) ? typeName : $"{candidateNamespace}.{typeName}";
			var result = metadataImport.TryFindTypeDefByName(fullTypeName, enclosingClass, out var mdTypeDef);
			if (result is Cor.S_OK) return mdTypeDef;
		}
		return null;
	}

	// https://github.com/Samsung/netcoredbg/blob/8b8b22200fecdb1aec5f47af63215462d8c79a4b/src/debugger/evaluator.cpp#L695
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool IsCompilerGeneratedFieldName(string fieldName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
		if (fieldName.Length > 1 && fieldName.StartsWith('<')) return true;
		if (fieldName.Length > 4 && fieldName.StartsWith("CS$<", StringComparison.Ordinal)) return true;
		return false;
	}

	public static mdProperty? GetPropertyWithName(this IMetaDataImport metaDataImport, mdTypeDef mdTypeDef, string propertyName)
	{
		var properties = metaDataImport.EnumProperties(mdTypeDef);

		foreach (var property in properties)
		{
			if (property.IsNil) continue;
			var propertyProps = metaDataImport.GetPropertyProps(property);
			if (propertyProps.szProperty == propertyName)
			{
				return property;
			}
		}

		return null;
	}

	public static bool HasAnyAttribute(this IMetaDataImport metadataImport, mdToken token, string[] attributeNames)
	{
		foreach (var attributeName in attributeNames)
		{
			if (metadataImport.TryGetCustomAttributeByName(token, attributeName, out _, out _) is Cor.S_OK)
			{
				return true;
			}
		}
		return false;
	}

	public static bool IsExtensionMethod(this IMetaDataImport metadataImport, mdToken token)
	{
		return metadataImport.HasAnyAttribute(token, [AttributeConstants.ExtensionMethodAttributeName]);
	}

	public static async Task<ICorDebugValue?> CallParameterlessInstanceMethodAsync(this ICorDebugEval eval, Func<Task<CorDebugManagedCallbackEventArgs>> processEventsUntilEvalEventFunc, EvalStatus evalStatus, ICorDebugFunction corDebugFunction, ICorDebugValue corDebugValue)
	{
		const bool isStatic = false;

		var typeParameterArgs = corDebugValue.ExactType.TypeParameters;

		// For instance properties, pass the object; for static, pass nothing. Must pass the original CorDebugReferenceValue, not the dereferenced one.
		ICorDebugValue[] corDebugValues = isStatic ? [] : [corDebugValue];
		var result = await eval.CallParameterizedFunctionAsync(processEventsUntilEvalEventFunc, evalStatus, corDebugFunction, typeParameterArgs.Length, typeParameterArgs, corDebugValues.Length, corDebugValues);
		return result;
	}

	public static async Task<ICorDebugValue?> CallParameterizedFunctionAsync(this ICorDebugEval eval, Func<Task<CorDebugManagedCallbackEventArgs>> processEventsUntilEvalEventFunc, EvalStatus evalStatus, ICorDebugFunction corDebugFunction, int typeParamCount, ICorDebugType[]? typeParameterArgs, int paramCount, ICorDebugValue[] corDebugValues, bool throwOnException = false)
	{
		// Ensure that the object passed in corDebugValues is a CorDebugReferenceValue (when containing object is an instance class), ie must not be dereferenced
		return await RunEvalAsync(eval, processEventsUntilEvalEventFunc, evalStatus, throwOnException,
			() => eval.CallParameterizedFunction(corDebugFunction, typeParamCount, typeParameterArgs, paramCount, corDebugValues),
			e =>
			{
				var getResultResult = e.Eval.TryGetResult(out var result);
				if (getResultResult is not Cor.CORDBG_S_FUNC_EVAL_HAS_NO_RESULT && result is null) Marshal.ThrowExceptionForHR(getResultResult);
				return result;
			});
	}

	public static async Task<ICorDebugValue?> NewParameterizedObjectNoConstructorAsync(this ICorDebugEval eval, Func<Task<CorDebugManagedCallbackEventArgs>> processEventsUntilEvalEventFunc, EvalStatus evalStatus, ICorDebugClass pClass, int nTypeArgs, ICorDebugType[]? ppTypeArgs, bool throwOnException = false)
	{
		return await RunEvalAsync(eval, processEventsUntilEvalEventFunc, evalStatus, throwOnException,
			() => eval.NewParameterizedObjectNoConstructor(pClass, nTypeArgs, ppTypeArgs),
			e => e.Eval.Result);
	}

	public static async Task<ICorDebugValue?> NewParameterizedObjectAsync(this ICorDebugEval eval, Func<Task<CorDebugManagedCallbackEventArgs>> processEventsUntilEvalEventFunc, EvalStatus evalStatus, ICorDebugFunction corDebugFunction, int nTypeArgs, ICorDebugType[]? ppTypeArgs, int argCount, ICorDebugValue[] argValues, bool throwOnException = false)
	{
		return await RunEvalAsync(eval, processEventsUntilEvalEventFunc, evalStatus, throwOnException,
			() => eval.NewParameterizedObject(corDebugFunction, nTypeArgs, ppTypeArgs, argCount, argValues),
			e => e.Eval.Result);
	}

	public static async Task<ICorDebugValue?> NewParameterizedArrayAsync(this ICorDebugEval eval, Func<Task<CorDebugManagedCallbackEventArgs>> processEventsUntilEvalEventFunc, EvalStatus evalStatus, ICorDebugType elementType, uint length, bool throwOnException = false)
	{
		return await RunEvalAsync(eval, processEventsUntilEvalEventFunc, evalStatus, throwOnException,
			() => eval.NewParameterizedArray(elementType, 1, [length], [0]),
			e => e.Eval.Result);
	}

	public static async Task<ICorDebugValue> NewStringAsync(this ICorDebugEval eval, Func<Task<CorDebugManagedCallbackEventArgs>> processEventsUntilEvalEventFunc, EvalStatus evalStatus, string str, bool throwOnException = false)
	{
		return (await RunEvalAsync(eval, processEventsUntilEvalEventFunc, evalStatus, throwOnException,
			() => eval.NewString(str),
			e => e.Eval.Result))!;
	}

	private static async Task<ICorDebugValue?> RunEvalAsync(ICorDebugEval eval, Func<Task<CorDebugManagedCallbackEventArgs>> processEventsUntilEvalEventFunc, EvalStatus evalStatus, bool throwOnException, Action startEval, Func<EvalCompleteCorDebugManagedCallbackEventArgs, ICorDebugValue?> onComplete)
	{
		ICorDebugValue? returnValue = null;

		startEval();

		evalStatus.IsRunning = true;
		try
		{
			eval.Thread.Process.Continue(false);
			var evalEvent = await processEventsUntilEvalEventFunc();
			if (evalEvent is EvalCompleteCorDebugManagedCallbackEventArgs completeEvent)
			{
				if (completeEvent.Eval != eval) throw new ManagedDebugger.EvalException("EvalComplete callback error - Eval does not match");
				returnValue = onComplete(completeEvent);
			}
			else if (evalEvent is EvalExceptionCorDebugManagedCallbackEventArgs exceptionEvent)
			{
				if (exceptionEvent.Eval != eval) throw new ManagedDebugger.EvalException("EvalException callback error - Eval does not match");
				var exceptionValue = exceptionEvent.Eval.Result ?? throw new ManagedDebugger.EvalException("EvalException callback error - Result is null");
				if (throwOnException)
				{
					try
					{
						throw new ManagedDebugger.EvalException($"Evaluation threw {ManagedDebugger.GetCorDebugTypeFriendlyName(exceptionValue.ExactType)}");
					}
					finally
					{
						if (exceptionValue is ICorDebugHandleValue handle) handle.TryDispose();
					}
				}
				returnValue = exceptionValue;
			}
			return returnValue;
		}
		finally
		{
			evalStatus.IsRunning = false;
		}
	}

	public static ICorDebugValue NewBooleanValue(this ICorDebugEval eval, bool value)
	{
		var corValue = eval.CreateValue(CorElementType.BOOLEAN, null);

		if (value is true && corValue is ICorDebugGenericValue genValue)
		{
			var size = genValue.Size;
			var valueData = new byte[size];
			valueData[0] = 1;
			unsafe
			{
				fixed (byte* p = valueData)
				{
					var ptr = (IntPtr)p;
					genValue.SetValue(ptr);
				}
			}
		}

		return corValue;
	}

	public static string ToDisplayName(this CorDebugInternalFrameType frameType)
	{
		return frameType switch
		{
			CorDebugInternalFrameType.STUBFRAME_M2U => "[Managed to Native Transition]",
			CorDebugInternalFrameType.STUBFRAME_U2M => "[Native to Managed Transition]",
			CorDebugInternalFrameType.STUBFRAME_APPDOMAIN_TRANSITION => "[Appdomain Transition]",
			CorDebugInternalFrameType.STUBFRAME_LIGHTWEIGHT_FUNCTION => "[Lightweight function]",
			CorDebugInternalFrameType.STUBFRAME_FUNC_EVAL => "[Func Eval]",
			CorDebugInternalFrameType.STUBFRAME_INTERNALCALL => "[Internal Call]",
			CorDebugInternalFrameType.STUBFRAME_CLASS_INIT => "[Class Init]",
			CorDebugInternalFrameType.STUBFRAME_EXCEPTION => "[Exception]",
			CorDebugInternalFrameType.STUBFRAME_SECURITY => "[Security]",
			CorDebugInternalFrameType.STUBFRAME_JIT_COMPILATION => "[JIT Compilation]",
			_ => "[Unknown]"
		};
	}
}
