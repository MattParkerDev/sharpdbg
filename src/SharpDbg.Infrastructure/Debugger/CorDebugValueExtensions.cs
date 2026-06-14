using System.Runtime.InteropServices;
using ClrDebug;

namespace SharpDbg.Infrastructure.Debugger;

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

	/// <summary>
	/// Use this until https://github.com/lordmilko/ClrDebug/pull/20 is resolved
	/// </summary>
	public static string GetStringWithoutBug(this CorDebugStringValue corDebugStringValue, int cchString)
	{
		TryGetString(corDebugStringValue, cchString, out var szStringResult).ThrowOnNotOK();
		return szStringResult!;
	}

	private static HRESULT TryGetString(CorDebugStringValue corDebugStringValue, int cchString, out string? szStringResult)
	{
		char[] chArray = new char[cchString];
		int pcchString;
		int num = (int)corDebugStringValue.Raw.GetString(cchString, out pcchString, chArray);
		if (num == 0)
		{
			szStringResult = ClrDebug.Extensions.CreateString(chArray, pcchString + 1);
			return (HRESULT)num;
		}
		szStringResult = null;
		return (HRESULT)num;
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
		CorDebugType? currentType = objectValue.ExactType;
		mdFieldDef foundFieldDef = default;
		CorDebugClass? foundClass = null;
		MetaDataImport? foundMetadata = null;

		// Find field on base type if necessary
		while (currentType is not null)
		{
			var cls = currentType.Class;
			var meta = cls.Module.GetMetaDataInterface().MetaDataImport;
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

		if (foundClass is null || foundMetadata is null || foundFieldDef.IsNil) return null;

		var isStatic = foundFieldDef.IsStatic(foundMetadata);
		var fieldCorDebugValue = isStatic ? foundClass.GetStaticFieldValue(foundFieldDef, ilFrame.Raw) : objectValue.GetFieldValue(foundClass.Raw, foundFieldDef);
		return fieldCorDebugValue;
	}

	public static async Task<CorDebugValue?> GetPropertyValue(this CorDebugValue objectValue, CorDebugManagedCallback callback, EvalStatus evalStatus, CorDebugILFrame ilFrame, string propertyName)
	{
		var unwrappedValue = objectValue.UnwrapDebugValueToObject();

		CorDebugType? currentType = unwrappedValue.ExactType;
		mdProperty foundPropertyDef = default;
		CorDebugClass? foundClass = null;
		MetaDataImport? foundMetadata = null;

		// Find property on base type if necessary
		while (currentType is not null)
		{
			var cls = currentType.Class;
			var meta = cls.Module.GetMetaDataInterface().MetaDataImport;
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
		var eval = ilFrame.Chain.Thread.CreateEval();

		// May not be correct, will need further testing
		var parameterizedContainingType = objectValue.ExactType;

		var typeParameterTypes = parameterizedContainingType.TypeParameters;
		var typeParameterArgs = typeParameterTypes.Select(t => t.Raw).ToArray();

		// For instance properties, pass the object; for static, pass nothing. Must pass the original CorDebugReferenceValue, not the dereferenced one.
		ICorDebugValue[] corDebugValues = isStatic ? [] : [objectValue!.Raw];

		var returnValue = await eval.CallParameterizedFunctionAsync(callback, evalStatus, getMethod, typeParameterTypes.Length, typeParameterArgs, corDebugValues.Length, corDebugValues);
		return returnValue;
	}

	public static CorDebugFunction? GetPropertySetter(this CorDebugObjectValue objectValue, string propertyName)
	{
		return null;
	}

	public static bool IsExceptionType(this CorDebugType corDebugType)
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
