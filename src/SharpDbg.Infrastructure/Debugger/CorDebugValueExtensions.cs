using System.Runtime.InteropServices;
using ICorDebugSharp;

namespace SharpDbg.Infrastructure.Debugger;

public static class CorDebugValueExtensions
{
	public static ICorDebugObjectValue UnwrapDebugValueToObject(this ICorDebugValue corDebugValue)
	{
		var unwrappedValue = corDebugValue.UnwrapDebugValue();
		if (unwrappedValue is ICorDebugObjectValue objectValue)
		{
			return objectValue;
		}
		throw new InvalidOperationException("CorDebugValue is not an CorDebugObjectValue");
	}

	public static ICorDebugValue UnwrapDebugValue(this ICorDebugValue corDebugValue)
	{
		var valueToCheck = corDebugValue;
		if (valueToCheck is ICorDebugReferenceValue { IsNull: false } refValue)
		{
			valueToCheck = refValue.Dereference();
		}
		if (valueToCheck is ICorDebugBoxValue boxValue)
		{
			valueToCheck = boxValue.Object;
		}

		return valueToCheck;
	}

	public static byte[] GetValueAsBytes(this ICorDebugGenericValue corDebugGenericValue)
	{
		IntPtr buffer = Marshal.AllocHGlobal(corDebugGenericValue.Size);
		try
		{
			corDebugGenericValue.GetValue(buffer);
			var result = new byte[corDebugGenericValue.Size];
			Marshal.Copy(buffer, result, 0, corDebugGenericValue.Size);
			return result;
		}
		finally
		{
			Marshal.FreeHGlobal(buffer);
		}
	}

	public static ICorDebugValue? GetClassFieldValue(this ICorDebugObjectValue objectValue, ICorDebugILFrame ilFrame, string fieldName)
	{
		ICorDebugType? currentType = objectValue.ExactType;
		mdFieldDef foundFieldDef = default;
		ICorDebugClass? foundClass = null;
		IMetaDataImport? foundMetadata = null;

		// Find field on base type if necessary
		while (currentType is not null)
		{
			var cls = currentType.Class;
			var meta = cls.Module.GetMetaDataInterface<IMetaDataImport>();
			var field = meta.EnumFieldsWithName(cls.Token, fieldName).SingleOrDefault();
			if (field.IsNil is false)
			{
				foundFieldDef = field;
				foundClass = cls;
				foundMetadata = meta;
				break;
			}
			currentType = currentType.Base;
		}

		if (foundClass is null || foundMetadata is null || currentType is null || foundFieldDef.IsNil) return null;

		var isStatic = foundFieldDef.IsStatic(foundMetadata);
		var isLiteral = foundFieldDef.IsLiteral(foundMetadata);
		var fieldCorDebugValue = isLiteral ? foundFieldDef.GetLiteralCorDebugValue(foundMetadata, ilFrame) : isStatic ? currentType.GetStaticFieldValue(foundFieldDef, ilFrame) : objectValue.GetFieldValue(foundClass, foundFieldDef);
		return fieldCorDebugValue;
	}

	public static ICorDebugGenericValue GetLiteralCorDebugValue(this mdFieldDef fieldDef, IMetaDataImport metadataImport, ICorDebugILFrame ilFrame)
	{
		var fieldProps = metadataImport.GetFieldProps(fieldDef);
		var ppValue = fieldProps.ppValue;
		var corElementType = fieldProps.pdwCPlusTypeFlag;
		var eval = ilFrame.Chain.Thread.CreateEval();
		var createdValue = eval.CreateValue(corElementType, null);
		if (createdValue is not ICorDebugGenericValue corDebugGenericValue) throw new InvalidOperationException("Expected a CorDebugGenericValue for literal value");
		corDebugGenericValue.SetValue(ppValue);
		return corDebugGenericValue;
	}

	public static async Task<ICorDebugValue?> GetPropertyValue(this ICorDebugValue objectValue, Func<Task<CorDebugManagedCallbackEventArgs>> processEventsUntilEvalEventFunc, EvalStatus evalStatus, ICorDebugILFrame ilFrame, string propertyName)
	{
		return (await GetPropertyValueWithSetter(objectValue, processEventsUntilEvalEventFunc, evalStatus, ilFrame, propertyName))?.Value;
	}

	public static async Task<(ICorDebugValue Value, ICorDebugFunction? SetterFunction)?> GetPropertyValueWithSetter(this ICorDebugValue objectValue, Func<Task<CorDebugManagedCallbackEventArgs>> processEventsUntilEvalEventFunc, EvalStatus evalStatus, ICorDebugILFrame ilFrame, string propertyName)
	{
		var unwrappedValue = objectValue.UnwrapDebugValueToObject();

		ICorDebugType? currentType = unwrappedValue.ExactType;
		mdProperty foundPropertyDef = default;
		ICorDebugClass? foundClass = null;
		IMetaDataImport? foundMetadata = null;

		// Find property on base type if necessary
		while (currentType is not null)
		{
			var cls = currentType.Class;
			var meta = cls.Module.GetMetaDataInterface<IMetaDataImport>();
			var prop = meta.GetPropertyWithName(cls.Token, propertyName);
			if (prop?.IsNil is false)
			{
				foundPropertyDef = prop.Value;
				foundClass = cls;
				foundMetadata = meta;
				break;
			}
			currentType = currentType.Base;
		}

		if (foundClass is null || foundMetadata is null || foundPropertyDef.IsNil) return null;

		var propertyProps = foundMetadata.GetPropertyProps(foundPropertyDef);
		// Get the get method for the property
		var getMethodDef = propertyProps.pmdGetter;
		if (getMethodDef == mdMethodDef.Nil) return null; // No get method

		// Get method attributes to check if it's static
		var getterMethodProps = foundMetadata.GetMethodProps(getMethodDef);
		var getterAttr = getterMethodProps.pdwAttr;

		var isStatic = getterAttr.IsMdStatic();

		var getMethod = foundClass.Module.GetFunctionFromToken(getMethodDef);
		var setMethodDef = propertyProps.pmdSetter;
		var setterFunction = setMethodDef == mdMethodDef.Nil ? null : foundClass.Module.GetFunctionFromToken(setMethodDef);
		var eval = ilFrame.Chain.Thread.CreateEval();

		// May not be correct, will need further testing
		var parameterizedContainingType = objectValue.ExactType;

		var typeParameterTypes = parameterizedContainingType.TypeParameters;

		// For instance properties, pass the object; for static, pass nothing. Must pass the original CorDebugReferenceValue, not the dereferenced one.
		ICorDebugValue[] corDebugValues = isStatic ? [] : [objectValue];

		var returnValue = await eval.CallParameterizedFunctionAsync(processEventsUntilEvalEventFunc, evalStatus, getMethod, typeParameterTypes.Length, typeParameterTypes, corDebugValues.Length, corDebugValues);
		return returnValue is null ? null : (returnValue, setterFunction);
	}

	/// Calls ICorDebugType.GetStaticFieldValue with a retry on CORDBG_E_STATIC_VAR_NOT_AVAILABLE
	/// This would occur if a type's static constructor had not been invoked yet
	public static async ValueTask<ICorDebugValue> GetStaticFieldValueAsync(this ICorDebugType type, Func<Task<CorDebugManagedCallbackEventArgs>> processEventsUntilEvalEventFunc, EvalStatus evalStatus, mdFieldDef fieldDef, ICorDebugILFrame ilFrame)
	{
		var result = type.TryGetStaticFieldValue(fieldDef, ilFrame, out var value);
		if (result is Cor.CORDBG_E_STATIC_VAR_NOT_AVAILABLE)
		{
			var typeParameters = type.TypeParameters;
			var eval = ilFrame.Chain.Thread.CreateEval();
			await eval.NewParameterizedObjectNoConstructorAsync(processEventsUntilEvalEventFunc, evalStatus, type.Class, typeParameters.Length, typeParameters);
			result = type.TryGetStaticFieldValue(fieldDef, ilFrame, out value);
		}

		Marshal.ThrowExceptionForHR(result);
		return value;
	}

	public static ICorDebugFunction? GetPropertySetter(this ICorDebugObjectValue objectValue, string propertyName)
	{
		return null;
	}

	public static bool IsExceptionType(this ICorDebugType corDebugType)
	{
		var type = corDebugType;
		while (type is not null)
		{
			if (ManagedDebugger.GetCorDebugTypeFriendlyName(type) == "System.Exception") return true;
			type = type.Base;
		}
		return false;
	}
}
