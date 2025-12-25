using System.Runtime.InteropServices;
using ClrDebug;

namespace DotnetDbg.Infrastructure.Debugger;

public static class CorDebugValueExtensions
{
	public static CorDebugObjectValue UnwrapDebugValueToObject(this CorDebugValue corDebugValue)
	{
		var unwrappedValue = corDebugValue.UnwrapDebugValue();
		if (unwrappedValue is CorDebugObjectValue objectValue)
		{
			return objectValue;
		}
		throw new InvalidOperationException("CorDebugValue is not an CorDebugObjectValue");
	}

	public static CorDebugValue UnwrapDebugValue(this CorDebugValue corDebugValue)
	{
		var valueToCheck = corDebugValue;
		if (valueToCheck is CorDebugReferenceValue { IsNull: false } refValue)
		{
			valueToCheck = refValue.Dereference();
		}
		if (valueToCheck is CorDebugBoxValue boxValue)
		{
			valueToCheck = boxValue.Object;
		}

		return valueToCheck;
	}

	public static byte[] GetValueAsBytes(this CorDebugGenericValue corDebugGenericValue)
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

	public static CorDebugValue? GetClassFieldValue(this CorDebugObjectValue objectValue, CorDebugILFrame ilFrame, string fieldName)
	{
		var corDebugClass = objectValue.Class;
		var module = corDebugClass.Module;
		var mdTypeDef = corDebugClass.Token;
		var metadataImport = module.GetMetaDataInterface().MetaDataImport;

		var mdFieldDef = metadataImport.EnumFieldsWithName(mdTypeDef, fieldName).SingleOrDefault();
		if (mdFieldDef.IsNil) return null;
		var isStatic = mdFieldDef.IsStatic(metadataImport);

		var fieldCorDebugValue = isStatic ? corDebugClass.GetStaticFieldValue(mdFieldDef, ilFrame.Raw) : objectValue.GetFieldValue(corDebugClass.Raw, mdFieldDef);
		return fieldCorDebugValue;
	}

	public static async Task<CorDebugValue?> GetPropertyValue(this CorDebugObjectValue objectValue, CorDebugILFrame ilFrame, CorDebugManagedCallback callback, string propertyName)
	{
		var corDebugClass = objectValue.Class;
		var metadataImport = corDebugClass.Module.GetMetaDataInterface().MetaDataImport;
		var mdProperty = metadataImport.GetPropertyWithName(corDebugClass.Token, propertyName);
		if (mdProperty is null || mdProperty.Value.IsNil) return null;

		var propertyProps = metadataImport.GetPropertyProps(mdProperty.Value);
		// Get the get method for the property
		var getMethodDef = propertyProps.pmdGetter;
		if (getMethodDef == mdMethodDef.Nil) return null; // No get method

		// Get method attributes to check if it's static
		var getterMethodProps = metadataImport.GetMethodProps(getMethodDef);
		var getterAttr = getterMethodProps.pdwAttr;

		bool isStatic = (getterAttr & CorMethodAttr.mdStatic) != 0;

		var getMethod = corDebugClass.Module.GetFunctionFromToken(getMethodDef);
		var eval = ilFrame.Chain.Thread.CreateEval();

		// May not be correct, will need further testing
		var parameterizedContainingType = corDebugClass.GetParameterizedType(
			isStatic ? CorElementType.Class : (objectValue?.Type ?? CorElementType.Class),
			0,
			[]);

		var typeParameterTypes = parameterizedContainingType.TypeParameters;
		var typeParameterArgs = typeParameterTypes.Select(t => t.Raw).ToArray();

		// For instance properties, pass the object; for static, pass nothing
		ICorDebugValue[] corDebugValues = isStatic ? [] : [objectValue!.Raw];

		var returnValue = await eval.CallParameterizedFunctionAsync(callback, getMethod, typeParameterTypes.Length, typeParameterArgs, corDebugValues.Length, corDebugValues, ilFrame);
		return returnValue;
	}

	public static CorDebugFunction? GetPropertySetter(this CorDebugObjectValue objectValue, string propertyName)
	{
		return null;
	}
}
