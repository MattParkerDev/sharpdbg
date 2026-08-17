using ICorDebugSharp;

namespace SharpDbg.Infrastructure.Debugger.ExpressionEvaluator.Cil;

// 🤖
internal sealed class CilValue
{
	private CilValue(object? value, ICorDebugValue? corValue, ICilLocation? location = null, ICilLocation? sourceLocation = null)
	{
		Value = value;
		CorValue = corValue;
		Location = location;
		SourceLocation = sourceLocation;
	}

	public object? Value { get; }
	public ICorDebugValue? CorValue { get; }
	public ICilLocation? Location { get; }
	public ICilLocation? SourceLocation { get; }
	public bool IsNull => Value is null && (CorValue is null || CorValue is ICorDebugReferenceValue { IsNull: true });

	public static CilValue FromPrimitive(object value) => new(value, null);
	public static CilValue FromVirtual(object value) => new(value, null);
	public static CilValue FromTypeToken(ResolvedCilType type, ICorDebugValue value) => new(type, value);
	public static CilValue FromCorValue(ICorDebugValue value)
	{
		var primitive = ReadPrimitive(value);
		return primitive is null ? new(null, value) : new(primitive, null);
	}
	public static CilValue FromLocation(ICilLocation location) => new(null, null, location);
	public static CilValue Null() => new(null, null);
	/// <summary>
	/// Wraps a debuggee value without collapsing primitives (e.g. strings) to host objects.
	/// Used for values being stored into the debuggee, where the underlying ICorDebugValue must be preserved.
	/// </summary>
	public static CilValue FromDebuggeeValue(ICorDebugValue value) => new(null, value);

	/// <summary>
	/// Returns the string text for either a host string (<see cref="Value"/>) or a debuggee string value
	/// (<see cref="CorValue"/>), without discarding the original debuggee reference.
	/// </summary>
	public string? GetStringText() => Value as string ?? (CorValue?.UnwrapDebugValue() as ICorDebugStringValue)?.String;

	public CilValue WithSourceLocation(ICilLocation location) => new(Value, CorValue, Location, location);
	public CilValue Dereference() => Location?.Read() ?? throw new InvalidOperationException("CIL value is not a managed location");

	public int AsInt32() => Value switch
	{
		bool value => value ? 1 : 0,
		char value => value,
		sbyte value => value,
		byte value => value,
		short value => value,
		ushort value => value,
		int value => value,
		uint value => unchecked((int)value),
		_ when TryReadValueTypeInteger(out var integer) => unchecked((int)integer),
		_ => throw new InvalidOperationException($"Value '{Value?.GetType().Name ?? "null"}' is not an int32 stack value")
	};

	public long AsInt64() => Value switch
	{
		long value => value,
		ulong value => unchecked((long)value),
		_ when TryReadValueTypeInteger(out var integer) => integer,
		_ => AsInt32()
	};

	public ulong AsUInt64() => Value switch
	{
		byte value => value,
		ushort value => value,
		uint value => value,
		ulong value => value,
		sbyte value => unchecked((ulong)value),
		short value => unchecked((ulong)value),
		int value => unchecked((uint)value),
		long value => unchecked((ulong)value),
		bool value => value ? 1UL : 0UL,
		char value => value,
		_ when TryReadValueTypeInteger(out var integer) => unchecked((ulong)integer),
		_ => throw new InvalidOperationException($"Value '{Value?.GetType().Name ?? "null"}' is not an integer stack value")
	};

	public bool TryGetInt64(out long value)
	{
		try
		{
			value = AsInt64();
			return true;
		}
		catch (InvalidOperationException)
		{
			value = 0;
			return false;
		}
	}

	public double AsFloat() => Value switch
	{
		float value => value,
		double value => value,
		_ => throw new InvalidOperationException($"Value '{Value?.GetType().Name ?? "null"}' is not a floating-point stack value")
	};

	public bool IsTrue() => CorValue is ICorDebugReferenceValue reference
		? !reference.IsNull
		: CorValue is not null
			? true
			: Value switch
			{
				null => false,
				bool value => value,
				float value => value != 0,
				double value => value != 0,
				long value => value != 0,
				ulong value => value != 0,
				string => true,
				_ => AsInt32() != 0
			};

	private static object? ReadPrimitive(ICorDebugValue value)
	{
		var unwrapped = value.UnwrapDebugValue();
		if (unwrapped is not ICorDebugGenericValue generic) return null;
		var data = generic.GetValueAsBytes();
		return generic.Type switch
		{
			CorElementType.BOOLEAN => data[0] != 0,
			CorElementType.CHAR => BitConverter.ToChar(data),
			CorElementType.I1 => unchecked((sbyte)data[0]),
			CorElementType.U1 => data[0],
			CorElementType.I2 => BitConverter.ToInt16(data),
			CorElementType.U2 => BitConverter.ToUInt16(data),
			CorElementType.I4 => BitConverter.ToInt32(data),
			CorElementType.U4 => BitConverter.ToUInt32(data),
			CorElementType.I8 => BitConverter.ToInt64(data),
			CorElementType.U8 => BitConverter.ToUInt64(data),
			CorElementType.R4 => BitConverter.ToSingle(data),
			CorElementType.R8 => BitConverter.ToDouble(data),
			CorElementType.I => IntPtr.Size == 8 ? BitConverter.ToInt64(data) : BitConverter.ToInt32(data),
			CorElementType.U => IntPtr.Size == 8 ? BitConverter.ToUInt64(data) : BitConverter.ToUInt32(data),
			_ => null
		};
	}

	private bool TryReadValueTypeInteger(out long value)
	{
		value = 0;
		if (CorValue?.UnwrapDebugValue() is not ICorDebugGenericValue { Type: CorElementType.VALUETYPE } generic) return false;
		var data = generic.GetValueAsBytes();
		value = data.Length switch
		{
			1 => data[0],
			2 => BitConverter.ToInt16(data),
			4 => BitConverter.ToInt32(data),
			8 => BitConverter.ToInt64(data),
			_ => 0
		};
		return data.Length is 1 or 2 or 4 or 8;
	}
}

internal interface ICilLocation
{
	CilValue Read();
	void Write(CilValue value);
}

internal sealed class CorDebugLocation(ICorDebugValue value) : ICilLocation
{
	public ICorDebugValue Value => value;
	public CilValue Read() => CilValue.FromCorValue(GetStorageValue());
	public void Write(CilValue source)
	{
		var storage = GetStorageValue();
		var destination = storage.UnwrapDebugValue();

		// Value-typed storage. Value-type locations may be surfaced as an ICorDebugReferenceValue (e.g. enum
		// locals), so the discriminating signal is the dereferenced destination being a value-type generic,
		// not whether storage itself is a reference.
		if (destination is ICorDebugGenericValue destinationGeneric && IsValueType(destinationGeneric.Type))
		{
			if (source.CorValue?.UnwrapDebugValue() is ICorDebugGenericValue sourceGeneric)
			{
				if (destinationGeneric.Size != sourceGeneric.Size) throw new InvalidOperationException("CIL value sizes do not match");
				var sourceData = sourceGeneric.GetValueAsBytes();
				unsafe
				{
					fixed (byte* pointer = sourceData) destinationGeneric.SetValue((nint)pointer);
				}
				return;
			}
			if (source.Value is not null)
			{
				var data = destinationGeneric.Type == CorElementType.VALUETYPE
					? CilValueEncoding.GetBytesForSize(source.Value, destinationGeneric.Size)
					: CilValueEncoding.GetBytes(source.Value, destinationGeneric.Type);
				unsafe
				{
					fixed (byte* pointer = data) destinationGeneric.SetValue((nint)pointer);
				}
				return;
			}
			if (source.IsNull)
			{
				var zeroed = new byte[destinationGeneric.Size];
				unsafe
				{
					fixed (byte* pointer = zeroed) destinationGeneric.SetValue((nint)pointer);
				}
				return;
			}
			throw new NotSupportedException("The CIL value cannot be stored in this debuggee location");
		}

		// Reference-typed location (string/class/object/array local, field or element). The value is written
		// into the slot itself - never into the dereferenced target - and null zeroes the slot. The dereferenced
		// destination for a non-null string slot is the string's data (generic STRING), which must never be
		// written to as bytes, which is why the slot is handled here rather than above.
		if (storage is ICorDebugReferenceValue destinationReference)
		{
			if (source.IsNull)
			{
				destinationReference.Value = default;
				return;
			}
			if (source.CorValue is ICorDebugReferenceValue sourceReference)
			{
				destinationReference.Value = sourceReference.Value;
				return;
			}
			if (source.CorValue is ICorDebugHeapValue2 sourceHeap)
			{
				var handle = sourceHeap.CreateHandle(CorDebugHandleType.HANDLE_STRONG);
				try
				{
					destinationReference.Value = handle.Value;
				}
				finally
				{
					handle.TryDispose();
				}
				return;
			}
			throw new NotSupportedException("Cannot store a non-reference CIL value in a reference debuggee location");
		}

		throw new NotSupportedException("The CIL value cannot be stored in this debuggee location");
	}

	private ICorDebugValue GetStorageValue() => value is ICorDebugReferenceValue { Type: CorElementType.BYREF } byRef
		? byRef.Dereference()
		: value;

	private static bool IsValueType(CorElementType elementType) => elementType is
		CorElementType.VALUETYPE or
		CorElementType.BOOLEAN or
		CorElementType.CHAR or
		CorElementType.I1 or
		CorElementType.U1 or
		CorElementType.I2 or
		CorElementType.U2 or
		CorElementType.I4 or
		CorElementType.U4 or
		CorElementType.I8 or
		CorElementType.U8 or
		CorElementType.R4 or
		CorElementType.R8 or
		CorElementType.I or
		CorElementType.U;
}

internal sealed class TemporaryLocation(CilValue initialValue) : ICilLocation
{
	private CilValue _value = initialValue;
	public CilValue Read() => _value;
	public void Write(CilValue value) => _value = value;
}

internal sealed class SyntheticVariableLocation(ICorDebugValue arrayReference) : ICilLocation
{
	public ICorDebugValue ArrayReference { get; } = arrayReference;
	public ICorDebugValue StorageValue => ((ICorDebugArrayValue)ArrayReference.UnwrapDebugValue()).GetElementAtPosition(0);
	public CilValue Read() => CilValue.FromCorValue(StorageValue);
	public void Write(CilValue value) => new CorDebugLocation(StorageValue).Write(value);
}

internal static class CilValueEncoding
{
	public static byte[] GetBytesForSize(object value, int size) => size switch
	{
		1 => [unchecked((byte)Convert.ToInt64(value))],
		2 => BitConverter.GetBytes(unchecked((short)Convert.ToInt64(value))),
		4 => BitConverter.GetBytes(unchecked((int)Convert.ToInt64(value))),
		8 => BitConverter.GetBytes(Convert.ToInt64(value)),
		_ => throw new NotSupportedException($"Cannot encode a primitive CIL value into a {size}-byte value type")
	};

	public static byte[] GetBytes(object value, CorElementType targetType) => targetType switch
	{
		CorElementType.BOOLEAN => [(bool)value ? (byte)1 : (byte)0],
		CorElementType.CHAR => BitConverter.GetBytes(Convert.ToChar(value)),
		CorElementType.I1 => [unchecked((byte)Convert.ToSByte(value))],
		CorElementType.U1 => [Convert.ToByte(value)],
		CorElementType.I2 => BitConverter.GetBytes(Convert.ToInt16(value)),
		CorElementType.U2 => BitConverter.GetBytes(Convert.ToUInt16(value)),
		CorElementType.I4 => BitConverter.GetBytes(Convert.ToInt32(value)),
		CorElementType.U4 => BitConverter.GetBytes(Convert.ToUInt32(value)),
		CorElementType.I8 => BitConverter.GetBytes(Convert.ToInt64(value)),
		CorElementType.U8 => BitConverter.GetBytes(Convert.ToUInt64(value)),
		CorElementType.R4 => BitConverter.GetBytes(Convert.ToSingle(value)),
		CorElementType.R8 => BitConverter.GetBytes(Convert.ToDouble(value)),
		_ => throw new NotSupportedException($"Cannot encode a primitive CIL value as '{targetType}'")
	};
}
