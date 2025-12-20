using ClrDebug;

namespace DotnetDbg.Infrastructure.Debugger;

public static class CorDebugValueExtensions
{
	public static CorDebugObjectValue UnwrapDebugValueToObject(this CorDebugValue corDebugValue)
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
		if (valueToCheck is not CorDebugObjectValue objectValue) throw new InvalidOperationException("Value is not an object value");
		return objectValue;
	}
}
