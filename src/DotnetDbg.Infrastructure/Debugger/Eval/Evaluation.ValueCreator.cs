using ClrDebug;

namespace DotnetDbg.Infrastructure.Debugger.Eval;

public partial class Evaluation
{
	public class ValueCreator
	{
		private readonly EvalData _evalData;

		public ValueCreator(EvalData evalData)
		{
			_evalData = evalData;
		}

		public async Task<CorDebugValue> CreatePrimitiveValue(CorElementType type, byte[]? valueData)
		{
			var eval = _evalData.Thread.CreateEval();
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
			var eval = _evalData.Thread.CreateEval();
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
			var eval = _evalData.Thread.CreateEval();
			return eval.CreateValue(CorElementType.Class, null);
		}

		public async Task<CorDebugValue> CreateValueType(CorDebugClass valueTypeClass, byte[]? valueData)
		{
			var eval = _evalData.Thread.CreateEval();
			var corValue = await eval.NewParameterizedObjectNoConstructorAsync(_evalData.ManagedCallback, valueTypeClass, 0, null, _evalData.ILFrame);

			if (valueData != null && corValue != null)
			{
				var unwrapped = corValue.UnwrapDebugValue();
				if (unwrapped is not CorDebugGenericValue genValue) throw new InvalidOperationException("Failed to create value type");
				unsafe
				{
					fixed (byte* p = valueData)
					{
						var ptr = (IntPtr)p;
						genValue.SetValue(ptr);
					}
				}
				return corValue;
			}

			throw new InvalidOperationException("Failed to create value type");
		}

		public async Task<CorDebugValue> CreateString(string str)
		{
			var eval = _evalData.Thread.CreateEval();
			return await eval.NewStringAsync(_evalData.ManagedCallback, str, _evalData.ILFrame);
		}
	}
}
