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
		// Dereference if it's a reference type
		var valueToCheck = corDebugValue;
		if (valueToCheck is CorDebugReferenceValue { IsNull: false } refValue)
		{
			valueToCheck = refValue.Dereference();
		}
		if (valueToCheck is CorDebugBoxValue boxValue) // may need to be more sophisticated/recursive
		{
			valueToCheck = boxValue.Object;
		}

		return valueToCheck;
	}
}
