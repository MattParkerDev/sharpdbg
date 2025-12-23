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
			var eval = await _evalData.Thread.CreateEvalAsync();
			var corValue = await eval.CreateValueAsync(type, null);

			if (valueData != null && corValue is CorDebugGenericValue genValue)
			{
				await genValue.SetValueAsync(valueData);
			}

			return corValue;
		}

		public async Task<CorDebugValue> CreateBooleanValue(bool value)
		{
			var eval = await _evalData.Thread.CreateEvalAsync();
			var corValue = await eval.CreateValueAsync(CorElementType.Boolean, null);

			if (value && corValue is CorDebugGenericValue genValue)
			{
				var size = await genValue.GetSizeAsync();
				var valueData = new byte[size];
				valueData[0] = 1;
				await genValue.SetValueAsync(valueData);
			}

			return corValue;
		}

		public async Task<CorDebugValue> CreateNullValue()
		{
			var eval = await _evalData.Thread.CreateEvalAsync();
			return await eval.CreateValueAsync(CorElementType.Class, null);
		}

		public async Task<CorDebugValue> CreateValueType(CorDebugClass valueTypeClass, byte[]? valueData)
		{
			var eval = await _evalData.Thread.CreateEvalAsync();

			if (eval is CorDebugEval2 eval2)
			{
				var corValue = await eval2.NewParameterizedObjectNoConstructorAsync(valueTypeClass, 0, null);

				if (valueData != null && corValue != null)
				{
					var unwrapped = corValue.UnwrapDebugValue();
					if (unwrapped is CorDebugGenericValue genValue)
					{
						await genValue.SetValueAsync(valueData);
					}
					return corValue;
				}
			}

			throw new InvalidOperationException("Failed to create value type");
		}

		public async Task<CorDebugValue> CreateString(string str)
		{
			var eval = await _evalData.Thread.CreateEvalAsync();
			return await eval.NewStringAsync(str);
		}
	}
}
