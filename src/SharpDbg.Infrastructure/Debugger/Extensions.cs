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

	public static async Task<ICorDebugValue?> CallParameterlessInstanceMethodAsync(this ICorDebugEval eval, CorDebugManagedCallback managedCallback, EvalStatus evalStatus, ICorDebugFunction corDebugFunction, ICorDebugValue corDebugValue)
	{
		const bool isStatic = false;

		var typeParameterArgs = corDebugValue.ExactType.TypeParameters;

		// For instance properties, pass the object; for static, pass nothing. Must pass the original CorDebugReferenceValue, not the dereferenced one.
		ICorDebugValue[] corDebugValues = isStatic ? [] : [corDebugValue];
		var result = await eval.CallParameterizedFunctionAsync(managedCallback, evalStatus, corDebugFunction, typeParameterArgs.Length, typeParameterArgs, corDebugValues.Length, corDebugValues);
		return result;
	}

	public static async Task<ICorDebugValue?> CallParameterizedFunctionAsync(this ICorDebugEval eval, CorDebugManagedCallback managedCallback, EvalStatus evalStatus, ICorDebugFunction corDebugFunction, int typeParamCount, ICorDebugType[]? typeParameterArgs, int paramCount, ICorDebugValue[] corDebugValues)
	{
		// Ensure that the object passed in corDebugValues is a CorDebugReferenceValue (when containing object is an instance class), ie must not be dereferenced
		return await RunEvalAsync(eval, managedCallback, evalStatus,
			() => eval.CallParameterizedFunction(corDebugFunction, typeParamCount, typeParameterArgs, paramCount, corDebugValues),
			e =>
			{
				var getResultResult = e.Eval.TryGetResult(out var result);
				if (getResultResult is not Cor.CORDBG_S_FUNC_EVAL_HAS_NO_RESULT && result is null) Marshal.ThrowExceptionForHR(getResultResult);
				return result;
			});
	}

	public static async Task<ICorDebugValue?> NewParameterizedObjectNoConstructorAsync(this ICorDebugEval eval, CorDebugManagedCallback managedCallback, EvalStatus evalStatus, ICorDebugClass pClass, int nTypeArgs, ICorDebugType[]? ppTypeArgs)
	{
		return await RunEvalAsync(eval, managedCallback, evalStatus,
			() => eval.NewParameterizedObjectNoConstructor(pClass, nTypeArgs, ppTypeArgs),
			e => e.Eval.Result);
	}

	public static async Task<ICorDebugValue?> NewParameterizedObjectAsync(this ICorDebugEval eval, CorDebugManagedCallback managedCallback, EvalStatus evalStatus, ICorDebugFunction corDebugFunction, int nTypeArgs, ICorDebugType[]? ppTypeArgs, int argCount, ICorDebugValue[] argValues)
	{
		return await RunEvalAsync(eval, managedCallback, evalStatus,
			() => eval.NewParameterizedObject(corDebugFunction, nTypeArgs, ppTypeArgs, argCount, argValues),
			e => e.Eval.Result);
	}

	public static async Task<ICorDebugValue> NewStringAsync(this ICorDebugEval eval, CorDebugManagedCallback managedCallback, EvalStatus evalStatus, string str)
	{
		return (await RunEvalAsync(eval, managedCallback, evalStatus,
			() => eval.NewString(str),
			e => e.Eval.Result))!;
	}

	private static async Task<ICorDebugValue?> RunEvalAsync(ICorDebugEval eval, CorDebugManagedCallback managedCallback, EvalStatus evalStatus, Action startEval, Func<EvalCompleteCorDebugManagedCallbackEventArgs, ICorDebugValue?> onComplete)
	{
		ICorDebugValue? returnValue = null;
		var evalCompleteTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		try
		{
			startEval();

			managedCallback.OnEvalComplete += OnEvalComplete;
			managedCallback.OnEvalException += OnEvalException;

			evalStatus.IsRunning = true;
			eval.Thread.Process.Continue(false);
			await evalCompleteTcs.Task;
			return returnValue;
		}
		finally
		{
			evalStatus.IsRunning = false;
			managedCallback.OnEvalComplete -= OnEvalComplete;
			managedCallback.OnEvalException -= OnEvalException;
		}
		void OnEvalComplete(object? s, EvalCompleteCorDebugManagedCallbackEventArgs e)
		{
			if (e.Eval != eval) return;
			returnValue = onComplete(e);
			evalCompleteTcs.SetResult();
		}
		void OnEvalException(object? sender, EvalExceptionCorDebugManagedCallbackEventArgs e)
		{
			if (e.Eval != eval) return;
			if (e.Eval.Result is null)
			{
				evalCompleteTcs.SetException(new ManagedDebugger.EvalException("EvalException callback error - Result is null"));
				return;
			}

			returnValue = e.Eval.Result;
			evalCompleteTcs.SetResult();
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
}
