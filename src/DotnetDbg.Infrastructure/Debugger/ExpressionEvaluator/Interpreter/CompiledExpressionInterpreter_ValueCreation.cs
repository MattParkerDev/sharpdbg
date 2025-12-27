using ClrDebug;

namespace DotnetDbg.Infrastructure.Debugger.ExpressionEvaluator.Interpreter;

public partial class CompiledExpressionInterpreter
{
	public async Task<CorDebugValue> CreatePrimitiveValue(CorElementType type, byte[]? valueData)
	{
		var eval = _context.Thread.CreateEval();
		var corValue = eval.CreateValue(type, null);

		if (valueData != null && corValue is CorDebugGenericValue genValue)
		{
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

	public async Task<CorDebugValue> CreateBooleanValue(bool value)
	{
		var eval = _context.Thread.CreateEval();
		var corValue = eval.CreateValue(CorElementType.Boolean, null);

		if (value && corValue is CorDebugGenericValue genValue)
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

	public async Task<CorDebugValue> CreateNullValue()
	{
		var eval = _context.Thread.CreateEval();
		return eval.CreateValue(CorElementType.Class, null);
	}

	public async Task<CorDebugValue> CreateValueType(CorDebugClass valueTypeClass, byte[]? valueData)
	{
		var eval = _context.Thread.CreateEval();
		var corValue = await eval.NewParameterizedObjectNoConstructorAsync(_debuggerManagedCallback, valueTypeClass, 0, null);

		if (valueData != null && corValue != null)
		{
			var unwrapped = corValue.UnwrapDebugValue();
			var unwrappedAsGeneric = unwrapped.As<CorDebugGenericValue>(); // a CorDebugObjectValue can also be a CorDebugGenericValue when it is a value class
			unsafe
			{
				fixed (byte* p = valueData)
				{
					var ptr = (IntPtr)p;
					unwrappedAsGeneric.SetValue(ptr);
				}
			}
			return corValue;
		}

		throw new InvalidOperationException("Failed to create value type");
	}

	public async Task<CorDebugValue> CreateString(string str)
	{
		var eval = _context.Thread.CreateEval();
		return await eval.NewStringAsync(_debuggerManagedCallback, str);
	}
}
