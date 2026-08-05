using ICorDebugSharp;

namespace SharpDbg.Infrastructure.Debugger.ExpressionEvaluator.Interpreter;

public partial class CompiledExpressionInterpreter
{
	private async Task<ICorDebugValue> CreatePrimitiveValue(CorElementType type, byte[]? valueData)
	{
		var eval = _context.Thread.CreateEval();
		var corValue = eval.CreateValue(type, null);

		if (valueData is not null && corValue is ICorDebugGenericValue genValue)
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

	private async Task<ICorDebugValue> CreateBooleanValue(bool value)
	{
		var eval = _context.Thread.CreateEval();
		var corValue = eval.NewBooleanValue(value);
		return corValue;
	}

	private async Task<ICorDebugValue> CreateNullValue()
	{
		var eval = _context.Thread.CreateEval();
		return eval.CreateValue(CorElementType.CLASS, null);
	}

	private async Task<ICorDebugValue> CreateValueType(ICorDebugClass valueTypeClass, byte[]? valueData)
	{
		var eval = _context.Thread.CreateEval();
		var corValue = await eval.NewParameterizedObjectNoConstructorAsync(_debugger.ProcessRuntimeEventsUntilEvalEvent, _debugger.EvalStatus, valueTypeClass, 0, null);

		if (valueData is not null && corValue is not null)
		{
			var unwrapped = corValue.UnwrapDebugValue();
			var unwrappedAsGeneric = (ICorDebugGenericValue)unwrapped; // a CorDebugObjectValue can also be a CorDebugGenericValue when it is a value class
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

	private async Task<ICorDebugValue> CreateString(string str)
	{
		var eval = _context.Thread.CreateEval();
		return await eval.NewStringAsync(_debugger.ProcessRuntimeEventsUntilEvalEvent, _debugger.EvalStatus, str);
	}
}
