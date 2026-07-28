using ICorDebugSharp;

namespace SharpDbg.Infrastructure.Debugger.ExpressionEvaluator.Interpreter;

public partial class CompiledExpressionInterpreter
{
	private async Task<ICorDebugValue> GetFrontStackEntryValue(LinkedList<EvalStackEntry> evalStack, bool needSetterData = false)
	{
		if (evalStack.First is null) throw new InvalidOperationException("Evaluation stack is empty");

		var entry = evalStack.First.Value;
		SetterData? setterData = needSetterData ? entry.SetterData : null;
		ICorDebugValue? optionalRootValue = null;
		if (_context.RootValue is not null && entry.CorDebugValue is null)
		{
			if (entry.CorDebugValue is not null) throw new InvalidOperationException("Both root value and entry value are set");
			if (entry.Identifiers is ["this"])
			{
				return _context.RootValue;
			}
			optionalRootValue = _context.RootValue;
		}
		var genericTypeArguments = entry.GenericTypeCache?.ToArray() ?? [];
		return await _debugger.ResolveIdentifiers(entry.Identifiers, _context.ThreadId, _context.StackDepth, entry.CorDebugValue, optionalRootValue, genericTypeArguments);
	}

	private async Task<ICorDebugType?> GetFrontStackEntryType(LinkedList<EvalStackEntry> evalStack)
	{
		if (evalStack.First is null)
			return null;

		var entry = evalStack.First.Value;

		return await ResolveIdentifiersForType(
			entry.CorDebugValue,
			entry.Identifiers
		);
	}

	private async Task<ICorDebugType?> ResolveIdentifiersForType(
		ICorDebugValue? baseValue,
		List<string> identifiers)
	{
		// TODO: implement type resolution?
		if (identifiers.Count == 0)
			return null;

		if (baseValue is not null)
		{
			throw new ArgumentException($"'{string.Join(".", identifiers)}' is a variable but is used like a type");
		}

		var typeName = string.Join(".", identifiers);
		throw new ArgumentException($"The type or namespace name '{typeName}' couldn't be found");
	}

	private async Task<ICorDebugValue> GetRealValueWithType(ICorDebugValue value)
	{
		var realValue = value.UnwrapDebugValue();
		var elemType = realValue.Type;

		if (elemType == CorElementType.STRING || elemType == CorElementType.CLASS)
		{
			return value;
		}

		return realValue;
	}

	private async Task<uint> GetElementIndex(ICorDebugValue indexValue)
	{
		var unwrapped = indexValue.UnwrapDebugValue();

		if (unwrapped is ICorDebugReferenceValue refValue && refValue.IsNull)
		{
			throw new ArgumentException("Index cannot be null");
		}

		if (unwrapped is not ICorDebugGenericValue genValue)
		{
			throw new ArgumentException("Index must be an integer type");
		}

		var size = genValue.Size;
		var data = genValue.GetValueAsBytes();
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

	private async Task<(byte[] Value, CorElementType Type)> GetOperandDataTypeByValue(ICorDebugValue value)
	{
		var unwrapped = value.UnwrapDebugValue();
		var elemType = unwrapped.Type;

		// if (elemType == CorElementType.String && value is CorDebugReferenceValue refValue && !refValue.IsNull)
		// {
		// 	var strValue = refValue.Dereference() as CorDebugStringValue;
		// 	return (value, elemType);
		// }

		if (unwrapped is not ICorDebugGenericValue genValue)
		{
			throw new ArgumentException("Value is not a primitive type");
		}

		var valueAsBytes = genValue.GetValueAsBytes();
		return (valueAsBytes, elemType);
	}
}
