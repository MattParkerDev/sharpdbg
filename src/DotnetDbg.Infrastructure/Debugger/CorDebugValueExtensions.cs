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

	public static CorDebugValue? GetPropertyValue(this CorDebugObjectValue objectValue, string propertyName)
	{
		return null;
	}

	public static CorDebugFunction? GetPropertySetter(this CorDebugObjectValue objectValue, string propertyName)
	{
		return null;
	}
}
