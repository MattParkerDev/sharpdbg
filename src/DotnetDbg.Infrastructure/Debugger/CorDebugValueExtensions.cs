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

	public static CorDebugValue? GetClassFieldValue(this CorDebugObjectValue objectValue, string fieldName)
	{
		return null;
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
