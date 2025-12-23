using ClrDebug;

namespace DotnetDbg.Infrastructure.Debugger.Eval;

public partial class Evaluation
{
	public class OperatorEvaluator
	{
		private readonly EvalData _evalData;
		private readonly ValueCreator _valueCreator;
		private readonly ExpressionExecutor _executor;

		public OperatorEvaluator(EvalData evalData)
		{
			_evalData = evalData;
			_valueCreator = new ValueCreator(evalData);
			_executor = new ExpressionExecutor(evalData);
		}

		public bool SupportedByCalculationDelegateType(CorElementType elemType)
		{
			return elemType switch
			{
				CorElementType.Boolean => true,
				CorElementType.U1 => true,
				CorElementType.I1 => true,
				CorElementType.Char => true,
				CorElementType.R8 => true,
				CorElementType.R4 => true,
				CorElementType.I4 => true,
				CorElementType.U4 => true,
				CorElementType.I8 => true,
				CorElementType.U8 => true,
				CorElementType.I2 => true,
				CorElementType.U2 => true,
				CorElementType.String => true,
				_ => false
			};
		}

		public async Task<CorDebugValue> CalculateTwoOperands(
			OperationType opType,
			LinkedList<EvalStackEntry> evalStack)
		{
			var value2 = await _executor.GetFrontStackEntryValue(evalStack);
			evalStack.RemoveFirst();

			var realValue2 = await _executor.GetRealValueWithType(value2!);
			var elemType2 = realValue2.Type;

			var value1 = await _executor.GetFrontStackEntryValue(evalStack);
			evalStack.RemoveFirst();

			var realValue1 = await _executor.GetRealValueWithType(value1);
			var elemType1 = realValue1.Type;

			if (elemType1 == CorElementType.ValueType || elemType2 == CorElementType.ValueType ||
				elemType1 == CorElementType.Class || elemType2 == CorElementType.Class)
			{
				var opName = GetOperatorName(opType);
				if (opName != null)
				{
					if (elemType1 == CorElementType.ValueType || elemType1 == CorElementType.Class)
					{
						var result = await CallBinaryOperator(opName, realValue1, realValue1, realValue2);
						if (result != null)
						{
							evalStack.First.Value.CorDebugValue = result;
							return result;
						}
					}

					if (elemType2 == CorElementType.ValueType || elemType2 == CorElementType.Class)
					{
						var result = await CallBinaryOperator(opName, realValue2, realValue1, realValue2);
						if (result != null)
						{
							evalStack.First.Value.CorDebugValue = result;
							return result;
						}
					}
				}

				throw new ArgumentException($"Operator '{GetOperatorSymbol(opType)}' cannot be applied to operands of these types");
			}

			return await CalculatePrimitiveOperands(opType, realValue1, realValue2, evalStack);
		}

		public async Task<CorDebugValue> CalculateOneOperand(
			OperationType opType,
			LinkedList<EvalStackEntry> evalStack)
		{
			var value = await _executor.GetFrontStackEntryValue(evalStack);
			var realValue = await _executor.GetRealValueWithType(value!);
			var elemType = realValue.Type;

			if (elemType == CorElementType.ValueType || elemType == CorElementType.Class)
			{
				var opName = GetUnaryOperatorName(opType);
				if (opName != null)
				{
					var result = await CallUnaryOperator(opName, realValue);
					if (result != null)
					{
						evalStack.First.Value.CorDebugValue = result;
						return result;
					}
				}

				throw new ArgumentException($"Operator '{GetOperatorSymbol(opType)}' cannot be applied to operand of this type");
			}

			return await CalculatePrimitiveOperand(opType, realValue, evalStack);
		}

		private async Task<CorDebugValue> CalculatePrimitiveOperands(
			OperationType opType,
			CorDebugValue value1,
			CorDebugValue value2,
			LinkedList<EvalStackEntry> evalStack)
		{
			var (data1, type1) = await _executor.GetOperandDataTypeByValue(value1);
			var (data2, type2) = await _executor.GetOperandDataTypeByValue(value2);

			var resultData = CalculatePrimitive(type1, type2, opType, data1, data2);
			var result = await CreateValueFromPrimitiveData(resultData);

			evalStack.First.Value.CorDebugValue = result;
			return result;
		}

		private async Task<CorDebugValue> CalculatePrimitiveOperand(
			OperationType opType,
			CorDebugValue value,
			LinkedList<EvalStackEntry> evalStack)
		{
			var (data, type) = await _executor.GetOperandDataTypeByValue(value);

			var resultData = CalculatePrimitiveUnary(type, opType, data);
			var result = await CreateValueFromPrimitiveData(resultData);

			evalStack.First.Value.CorDebugValue = result;
			return result;
		}

		private async Task<CorDebugValue?> CallBinaryOperator(
			string opName,
			CorDebugValue baseValue,
			CorDebugValue arg1,
			CorDebugValue arg2)
		{
			if (baseValue is not CorDebugObjectValue objectValue)
				return null;

			var corDebugFunction = await FindOperatorMethod(objectValue, opName, 2);
			if (corDebugFunction == null) return null;

			var eval = _evalData.Thread.CreateEval();
			ICorDebugValue[] evalArgs = [arg1.Raw, arg2.Raw];
			return await eval.CallParameterizedFunctionAsync(_evalData.ManagedCallback, corDebugFunction, 0, null, evalArgs.Length, evalArgs, _evalData.ILFrame);
		}

		private async Task<CorDebugValue?> CallUnaryOperator(
			string opName,
			CorDebugValue baseValue)
		{
			if (baseValue is not CorDebugObjectValue objectValue)
				return null;

			var corDebugFunction = await FindOperatorMethod(objectValue, opName, 1);
			if (corDebugFunction == null)
				return null;

			var eval = _evalData.Thread.CreateEval();
			ICorDebugValue[] evalArgs = [baseValue.Raw];
			return await eval.CallParameterizedFunctionAsync(_evalData.ManagedCallback, corDebugFunction, 0, null, evalArgs.Length, evalArgs, _evalData.ILFrame);
		}

		private async Task<CorDebugFunction?> FindOperatorMethod(
			CorDebugObjectValue objectValue,
			string opName,
			int paramCount)
		{
			var objClass = objectValue.Class;
			var module = objClass.Module;
			var metaDataImport = module.GetMetaDataInterface().MetaDataImport;
			var classToken = objClass.Token;

			var methods = metaDataImport.EnumMethods(classToken);
			foreach (var methodToken in methods)
			{
				var methodDef = metaDataImport.GetMethodProps(methodToken);
				if (methodDef.szMethod == opName && methodToken.IsStatic(metaDataImport))
				{
					var method = module.GetFunctionFromToken(methodToken);
					return method;
				}
			}

			return null;
		}

		private async Task<CorDebugValue> CreateValueFromPrimitiveData(byte[] data)
		{
			if (data.Length == 1)
				return await _valueCreator.CreatePrimitiveValue(CorElementType.U1, data);
			if (data.Length == 2)
				return await _valueCreator.CreatePrimitiveValue(CorElementType.I2, data);
			if (data.Length == 4)
				return await _valueCreator.CreatePrimitiveValue(CorElementType.I4, data);
			if (data.Length == 8)
				return await _valueCreator.CreatePrimitiveValue(CorElementType.I8, data);

			throw new ArgumentException("Unknown primitive data size");
		}

		private byte[] CalculatePrimitive(
			CorElementType type1,
			CorElementType type2,
			OperationType opType,
			byte[] data1,
			byte[] data2)
		{
			if (type1 == CorElementType.R8 || type2 == CorElementType.R8)
			{
				var d1 = BitConverter.ToDouble(data1, 0);
				var d2 = BitConverter.ToDouble(data2, 0);
				var result = opType switch
				{
					OperationType.AddExpression => d1 + d2,
					OperationType.SubtractExpression => d1 - d2,
					OperationType.MultiplyExpression => d1 * d2,
					OperationType.DivideExpression => d1 / d2,
					OperationType.ModuloExpression => d1 % d2,
					OperationType.EqualsExpression => d1 == d2 ? 1.0 : 0.0,
					OperationType.NotEqualsExpression => d1 != d2 ? 1.0 : 0.0,
					OperationType.LessThanExpression => d1 < d2 ? 1.0 : 0.0,
					OperationType.GreaterThanExpression => d1 > d2 ? 1.0 : 0.0,
					OperationType.LessThanOrEqualExpression => d1 <= d2 ? 1.0 : 0.0,
					OperationType.GreaterThanOrEqualExpression => d1 >= d2 ? 1.0 : 0.0,
					_ => throw new ArgumentException($"Unsupported operation: {opType}")
				};
				return BitConverter.GetBytes(result);
			}

			if (type1 == CorElementType.R4 || type2 == CorElementType.R4)
			{
				var f1 = BitConverter.ToSingle(data1, 0);
				var f2 = BitConverter.ToSingle(data2, 0);
				var result = opType switch
				{
					OperationType.AddExpression => f1 + f2,
					OperationType.SubtractExpression => f1 - f2,
					OperationType.MultiplyExpression => f1 * f2,
					OperationType.DivideExpression => f1 / f2,
					OperationType.ModuloExpression => f1 % f2,
					OperationType.EqualsExpression => f1 == f2 ? 1.0f : 0.0f,
					OperationType.NotEqualsExpression => f1 != f2 ? 1.0f : 0.0f,
					OperationType.LessThanExpression => f1 < f2 ? 1.0f : 0.0f,
					OperationType.GreaterThanExpression => f1 > f2 ? 1.0f : 0.0f,
					OperationType.LessThanOrEqualExpression => f1 <= f2 ? 1.0f : 0.0f,
					OperationType.GreaterThanOrEqualExpression => f1 >= f2 ? 1.0f : 0.0f,
					_ => throw new ArgumentException($"Unsupported operation: {opType}")
				};
				return BitConverter.GetBytes(result);
			}

			var l1 = BitConverter.ToInt64(data1, 0);
			var l2 = BitConverter.ToInt64(data2, 0);
			var result64 = opType switch
			{
				OperationType.AddExpression => l1 + l2,
				OperationType.SubtractExpression => l1 - l2,
				OperationType.MultiplyExpression => l1 * l2,
				OperationType.DivideExpression => l1 / l2,
				OperationType.ModuloExpression => l1 % l2,
				OperationType.LeftShiftExpression => l1 << (int)l2,
				OperationType.RightShiftExpression => l1 >> (int)l2,
				OperationType.BitwiseAndExpression => l1 & l2,
				OperationType.BitwiseOrExpression => l1 | l2,
				OperationType.ExclusiveOrExpression => l1 ^ l2,
				OperationType.EqualsExpression => l1 == l2 ? 1 : 0,
				OperationType.NotEqualsExpression => l1 != l2 ? 1 : 0,
				OperationType.LessThanExpression => l1 < l2 ? 1 : 0,
				OperationType.GreaterThanExpression => l1 > l2 ? 1 : 0,
				OperationType.LessThanOrEqualExpression => l1 <= l2 ? 1 : 0,
				OperationType.GreaterThanOrEqualExpression => l1 >= l2 ? 1 : 0,
				_ => throw new ArgumentException($"Unsupported operation: {opType}")
			};
			return BitConverter.GetBytes(result64);
		}

		private byte[] CalculatePrimitiveUnary(
			CorElementType type,
			OperationType opType,
			byte[] data)
		{
			if (type == CorElementType.R8)
			{
				var d = BitConverter.ToDouble(data, 0);
				var result = opType switch
				{
					OperationType.UnaryPlusExpression => +d,
					OperationType.UnaryMinusExpression => -d,
					OperationType.LogicalNotExpression => (d == 0.0) ? 1.0 : 0.0,
					_ => throw new ArgumentException($"Unsupported operation: {opType}")
				};
				return BitConverter.GetBytes(result);
			}

			if (type == CorElementType.R4)
			{
				var f = BitConverter.ToSingle(data, 0);
				var result = opType switch
				{
					OperationType.UnaryPlusExpression => +f,
					OperationType.UnaryMinusExpression => -f,
					OperationType.LogicalNotExpression => (f == 0.0f) ? 1.0f : 0.0f,
					_ => throw new ArgumentException($"Unsupported operation: {opType}")
				};
				return BitConverter.GetBytes(result);
			}

			var l = BitConverter.ToInt64(data, 0);
			var result64 = opType switch
			{
				OperationType.UnaryPlusExpression => +l,
				OperationType.UnaryMinusExpression => -l,
				OperationType.BitwiseNotExpression => ~l,
				OperationType.LogicalNotExpression => (l == 0) ? 1 : 0,
				_ => throw new ArgumentException($"Unsupported operation: {opType}")
			};
			return BitConverter.GetBytes(result64);
		}

		private string? GetOperatorName(OperationType opType)
		{
			return opType switch
			{
				OperationType.AddExpression => "op_Addition",
				OperationType.SubtractExpression => "op_Subtraction",
				OperationType.MultiplyExpression => "op_Multiply",
				OperationType.DivideExpression => "op_Division",
				OperationType.ModuloExpression => "op_Modulus",
				OperationType.RightShiftExpression => "op_RightShift",
				OperationType.LeftShiftExpression => "op_LeftShift",
				OperationType.LogicalAndExpression => "op_LogicalAnd",
				OperationType.LogicalOrExpression => "op_LogicalOr",
				OperationType.ExclusiveOrExpression => "op_ExclusiveOr",
				OperationType.BitwiseAndExpression => "op_BitwiseAnd",
				OperationType.BitwiseOrExpression => "op_BitwiseOr",
				OperationType.EqualsExpression => "op_Equality",
				OperationType.NotEqualsExpression => "op_Inequality",
				OperationType.LessThanExpression => "op_LessThan",
				OperationType.GreaterThanExpression => "op_GreaterThan",
				OperationType.LessThanOrEqualExpression => "op_LessThanOrEqual",
				OperationType.GreaterThanOrEqualExpression => "op_GreaterThanOrEqual",
				_ => null
			};
		}

		private string? GetUnaryOperatorName(OperationType opType)
		{
			return opType switch
			{
				OperationType.LogicalNotExpression => "op_LogicalNot",
				OperationType.BitwiseNotExpression => "op_OnesComplement",
				OperationType.UnaryPlusExpression => "op_UnaryPlus",
				OperationType.UnaryMinusExpression => "op_UnaryNegation",
				_ => null
			};
		}

		private string GetOperatorSymbol(OperationType opType)
		{
			return opType switch
			{
				OperationType.AddExpression => "+",
				OperationType.SubtractExpression => "-",
				OperationType.MultiplyExpression => "*",
				OperationType.DivideExpression => "/",
				OperationType.ModuloExpression => "%",
				OperationType.RightShiftExpression => ">>",
				OperationType.LeftShiftExpression => "<<",
				OperationType.LogicalAndExpression => "&&",
				OperationType.LogicalOrExpression => "||",
				OperationType.ExclusiveOrExpression => "^",
				OperationType.BitwiseAndExpression => "&",
				OperationType.BitwiseOrExpression => "|",
				OperationType.EqualsExpression => "==",
				OperationType.NotEqualsExpression => "!=",
				OperationType.LessThanExpression => "<",
				OperationType.GreaterThanExpression => ">",
				OperationType.LessThanOrEqualExpression => "<=",
				OperationType.GreaterThanOrEqualExpression => ">=",
				OperationType.LogicalNotExpression => "!",
				OperationType.BitwiseNotExpression => "~",
				OperationType.UnaryPlusExpression => "+",
				OperationType.UnaryMinusExpression => "-",
				_ => opType.ToString()
			};
		}
	}
}
