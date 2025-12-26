using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using ClrDebug;

namespace DotnetDbg.Infrastructure.Debugger;

public static class Extensions
{
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool IsStatic(this mdFieldDef mdFieldDef, MetaDataImport metadataImport)
	{
		var fieldProps = metadataImport.GetFieldProps(mdFieldDef);
		var isStatic = (fieldProps.pdwAttr & CorFieldAttr.fdStatic) != 0;
		return isStatic;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool IsStatic(this mdProperty mdProperty, MetaDataImport metadataImport)
	{
		var propertyProps = metadataImport.GetPropertyProps(mdProperty);
		var getterMethodProps = metadataImport.GetMethodProps(propertyProps.pmdGetter);
		var getterAttr = getterMethodProps.pdwAttr;

		var isStatic = (getterAttr & CorMethodAttr.mdStatic) != 0;
		return isStatic;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool IsStatic(this mdMethodDef methodToken, MetaDataImport metaDataImport)
	{
		var methodProps = metaDataImport.GetMethodProps(methodToken);
		var isStatic = (methodProps.pdwAttr & CorMethodAttr.mdStatic) != 0;
		return isStatic;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static mdTypeDef? FindTypeDefByNameOrNull(this MetaDataImport metadataImport, string typeName, mdToken enclosingClass)
	{
		var result = metadataImport.TryFindTypeDefByName(typeName, enclosingClass, out var mdTypeDef);
		if (result is HRESULT.S_OK) return mdTypeDef;
		return null;
	}

	public static mdTypeDef? FindTypeDefByNameOrNullInCandidateNamespaces(this MetaDataImport metadataImport, string typeName, mdToken enclosingClass, ImmutableArray<string> candidateNamespaces)
	{
		foreach (var candidateNamespace in candidateNamespaces)
		{
			var fullTypeName = string.IsNullOrEmpty(candidateNamespace) ? typeName : $"{candidateNamespace}.{typeName}";
			var result = metadataImport.TryFindTypeDefByName(fullTypeName, enclosingClass, out var mdTypeDef);
			if (result is HRESULT.S_OK) return mdTypeDef;
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

	public static mdProperty? GetPropertyWithName(this MetaDataImport metaDataImport, mdTypeDef mdTypeDef, string propertyName)
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

	public static async Task<CorDebugValue?> CallParameterizedFunctionAsync(this CorDebugEval eval, CorDebugManagedCallback managedCallback, CorDebugFunction corDebugFunction, int typeParamCount, ICorDebugType[]? typeParameterArgs, int paramCount, ICorDebugValue[] corDebugValues, CorDebugILFrame ilFrame)
	{
		CorDebugValue? returnValue = null;
		var evalCompleteTcs = new TaskCompletionSource();
		try
		{
			// Ensure that the object passed in corDebugValues is a CorDebugReferenceValue (when containing object is an instance class), ie must not be dereferenced
			eval.CallParameterizedFunction(corDebugFunction.Raw, typeParamCount, typeParameterArgs, paramCount, corDebugValues);

			managedCallback.OnEvalComplete += OnCallbacksOnOnEvalComplete;
			managedCallback.OnEvalException += CallbacksOnOnEvalException;

			ilFrame.Chain.Thread.Process.Continue(false);
			await evalCompleteTcs.Task;
			return returnValue;
		}
		finally
		{
			managedCallback.OnEvalComplete -= OnCallbacksOnOnEvalComplete;
			managedCallback.OnEvalException -= CallbacksOnOnEvalException;
		}
		void OnCallbacksOnOnEvalComplete(object? s, EvalCompleteCorDebugManagedCallbackEventArgs e)
		{
			if (e.Eval.Raw != eval.Raw) return;
			returnValue = e.Eval.Result;
			evalCompleteTcs.SetResult();
		}
		void CallbacksOnOnEvalException(object? sender, EvalExceptionCorDebugManagedCallbackEventArgs e)
		{
			if (e.Eval.Raw != eval.Raw) return;
			if (e.Eval.Result is null)
			{
				var exception = new ManagedDebugger.EvalException($"EvalException callback error - Result is null");
				evalCompleteTcs.SetException(exception);
				return;
			}

			returnValue = e.Eval.Result;
			evalCompleteTcs.SetResult();
		}
	}

	public static async Task<CorDebugValue?> NewParameterizedObjectNoConstructorAsync(this CorDebugEval eval, CorDebugManagedCallback managedCallback, CorDebugClass pClass, int nTypeArgs, ICorDebugType[]? ppTypeArgs, CorDebugILFrame ilFrame)
	{
		CorDebugValue? returnValue = null;
		var evalCompleteTcs = new TaskCompletionSource();
		try
		{
			eval.NewParameterizedObjectNoConstructor(pClass.Raw, nTypeArgs, ppTypeArgs);

			managedCallback.OnEvalComplete += OnCallbacksOnOnEvalComplete;
			managedCallback.OnEvalException += CallbacksOnOnEvalException;

			ilFrame.Chain.Thread.Process.Continue(false);
			await evalCompleteTcs.Task;
			return returnValue;
		}
		finally
		{
			managedCallback.OnEvalComplete -= OnCallbacksOnOnEvalComplete;
			managedCallback.OnEvalException -= CallbacksOnOnEvalException;
		}
		void OnCallbacksOnOnEvalComplete(object? s, EvalCompleteCorDebugManagedCallbackEventArgs e)
		{
			if (e.Eval.Raw != eval.Raw) return;
			returnValue = e.Eval.Result;
			evalCompleteTcs.SetResult();
		}
		void CallbacksOnOnEvalException(object? sender, EvalExceptionCorDebugManagedCallbackEventArgs e)
		{
			if (e.Eval.Raw != eval.Raw) return;
			if (e.Eval.Result is null)
			{
				var exception = new ManagedDebugger.EvalException($"EvalException callback error - Result is null");
				evalCompleteTcs.SetException(exception);
				return;
			}

			returnValue = e.Eval.Result;
			evalCompleteTcs.SetResult();
		}
	}

	public static async Task<CorDebugValue> NewStringAsync(this CorDebugEval eval, CorDebugManagedCallback managedCallback, string str, CorDebugILFrame ilFrame)
	{
		CorDebugValue? returnValue = null;
		var evalCompleteTcs = new TaskCompletionSource();
		try
		{
			eval.NewString(str);

			managedCallback.OnEvalComplete += OnCallbacksOnOnEvalComplete;
			managedCallback.OnEvalException += CallbacksOnOnEvalException;

			ilFrame.Chain.Thread.Process.Continue(false);
			await evalCompleteTcs.Task;
			return returnValue!;
		}
		finally
		{
			managedCallback.OnEvalComplete -= OnCallbacksOnOnEvalComplete;
			managedCallback.OnEvalException -= CallbacksOnOnEvalException;
		}
		void OnCallbacksOnOnEvalComplete(object? s, EvalCompleteCorDebugManagedCallbackEventArgs e)
		{
			if (e.Eval.Raw != eval.Raw) return;
			returnValue = e.Eval.Result;
			evalCompleteTcs.SetResult();
		}
		void CallbacksOnOnEvalException(object? sender, EvalExceptionCorDebugManagedCallbackEventArgs e)
		{
			if (e.Eval.Raw != eval.Raw) return;
			if (e.Eval.Result is null)
			{
				var exception = new ManagedDebugger.EvalException($"EvalException callback error - Result is null");
				evalCompleteTcs.SetException(exception);
				return;
			}

			returnValue = e.Eval.Result;
			evalCompleteTcs.SetResult();
		}
	}
}
