using System.Runtime.InteropServices;
using ClrDebug;

namespace DotnetDbg.Infrastructure.Debugger.Eval;

public partial class Evaluation
{
	public class ExpressionExecutor
	{
		private readonly EvalData _evalData;
		private readonly ValueCreator _valueCreator;

		public ExpressionExecutor(EvalData evalData)
		{
			_evalData = evalData;
			_valueCreator = new ValueCreator(evalData);
		}

		public async Task<CorDebugValue?> GetFrontStackEntryValue(LinkedList<EvalStackEntry> evalStack, bool needSetterData = false)
		{
			if (evalStack.First == null)
				return null;

			var entry = evalStack.First.Value;
			SetterData? setterData = needSetterData ? entry.SetterData : null;

			return await ResolveIdentifiers(
				_evalData.Thread,
				_evalData.FrameLevel,
				entry.CorDebugValue,
				setterData,
				entry.Identifiers
			);
		}

		public async Task<CorDebugType?> GetFrontStackEntryType(LinkedList<EvalStackEntry> evalStack)
		{
			if (evalStack.First == null)
				return null;

			var entry = evalStack.First.Value;

			return await ResolveIdentifiersForType(
				_evalData.Thread,
				_evalData.FrameLevel,
				entry.CorDebugValue,
				entry.Identifiers
			);
		}

		public async Task<CorDebugValue?> ResolveIdentifiers(
			CorDebugThread thread,
			int frameLevel,
			CorDebugValue? baseValue,
			SetterData? setterData,
			List<string> identifiers)
		{
			if (identifiers.Count == 0)
				return baseValue;

			var current = baseValue;
			var currentSetterData = setterData;

			foreach (var identifier in identifiers)
			{
				if (current == null)
				{
					throw new ArgumentException($"The name '{identifier}' does not exist in the current context");
				}

				var unwrapped = current.UnwrapDebugValue();

				if (unwrapped is CorDebugObjectValue objectValue)
				{
					var field = await objectValue.GetClassFieldValueAsync(identifier);
					if (field != null)
					{
						current = field;
						currentSetterData = new SetterData { OwnerValue = current };
						continue;
					}

					var property = await objectValue.GetPropertyValueAsync(identifier);
					if (property != null)
					{
						current = property;
						currentSetterData = new SetterData { OwnerValue = current, SetterFunction = await objectValue.GetPropertySetterAsync(identifier) };
						continue;
					}
				}

				throw new ArgumentException($"The name '{identifier}' does not exist in the current context");
			}

			return current;
		}

		public async Task<CorDebugType?> ResolveIdentifiersForType(
			CorDebugThread thread,
			int frameLevel,
			CorDebugValue? baseValue,
			List<string> identifiers)
		{
			if (identifiers.Count == 0)
				return null;

			if (baseValue != null)
			{
				throw new ArgumentException($"'{string.Join(".", identifiers)}' is a variable but is used like a type");
			}

			var typeName = string.Join(".", identifiers);
			throw new ArgumentException($"The type or namespace name '{typeName}' couldn't be found");
		}

		public async Task<CorDebugValue> GetRealValueWithType(CorDebugValue value)
		{
			var realValue = value.UnwrapDebugValue();
			var elemType = realValue.Type;

			if (elemType == CorElementType.String || elemType == CorElementType.Class)
			{
				return value;
			}

			return realValue;
		}

		public async Task<uint> GetElementIndex(CorDebugValue indexValue)
		{
			var unwrapped = indexValue.UnwrapDebugValue();

			if (unwrapped is CorDebugReferenceValue refValue && refValue.IsNull)
			{
				throw new ArgumentException("Index cannot be null");
			}

			if (unwrapped is not CorDebugGenericValue genValue)
			{
				throw new ArgumentException("Index must be an integer type");
			}

			var size = genValue.Size;
			var data = await genValue.GetValueAsync();
			var elemType = unwrapped.Type;

			return elemType switch
			{
				CorElementType.I1 => unchecked((uint)(sbyte)data[0]),
				CorElementType.U1 => data[0],
				CorElementType.I2 => unchecked((uint)BitConverter.ToInt16(data, 0)),
				CorElementType.U2 => BitConverter.ToUInt16(data, 0),
				CorElementType.I4 => unchecked((uint)BitConverter.ToInt32(data, 0)),
				CorElementType.U4 => BitConverter.ToUInt32(data, 0),
				CorElementType.I8 => unchecked((uint)BitConverter.ToInt64(data, 0)),
				CorElementType.U8 => unchecked((uint)BitConverter.ToUInt64(data, 0)),
				_ => throw new ArgumentException("Invalid index type")
			};
		}

		public async Task<(CorDebugValue Value, CorElementType Type)> GetOperandDataTypeByValue(CorDebugValue value)
		{
			var unwrapped = value.UnwrapDebugValue();
			var elemType = unwrapped.Type;

			if (elemType == CorElementType.String && value is CorDebugReferenceValue refValue && !refValue.IsNull)
			{
				var strValue = refValue.Dereference() as CorDebugStringValue;
				return (value, elemType);
			}

			if (unwrapped is not CorDebugGenericValue genValue)
			{
				throw new ArgumentException("Value is not a primitive type");
			}

			return (unwrapped, elemType);
		}
	}
}
