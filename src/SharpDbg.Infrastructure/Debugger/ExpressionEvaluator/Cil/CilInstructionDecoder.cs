using System.Buffers.Binary;
using System.Reflection.Emit;

namespace SharpDbg.Infrastructure.Debugger.ExpressionEvaluator.Cil;

internal readonly record struct CilInstruction(int Offset, OpCode OpCode, object? Operand);

internal static class CilInstructionDecoder
{
	private static readonly IReadOnlyDictionary<short, OpCode> _opCodes = typeof(OpCodes)
		.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
		.Where(f => f.FieldType == typeof(OpCode))
		.Select(f => (OpCode)f.GetValue(null)!)
		.ToDictionary(op => op.Value);

	public static IReadOnlyList<CilInstruction> Decode(byte[] il)
	{
		var result = new List<CilInstruction>();
		var position = 0;
		while (position < il.Length)
		{
			var offset = position;
			short value = il[position++] == 0xfe
				? unchecked((short)(0xfe00 | il[position++]))
				: il[offset];
			if (!_opCodes.TryGetValue(value, out var opCode))
			{
				throw new NotSupportedException($"Unknown CIL opcode 0x{value:X4} at IL_{offset:X4}");
			}

			object? operand = opCode.OperandType switch
			{
				OperandType.InlineNone => null,
				OperandType.ShortInlineI => unchecked((sbyte)il[position++]),
				OperandType.InlineI => ReadInt32(il, ref position),
				OperandType.InlineI8 => ReadInt64(il, ref position),
				OperandType.ShortInlineR => ReadSingle(il, ref position),
				OperandType.InlineR => ReadDouble(il, ref position),
				OperandType.ShortInlineVar => il[position++],
				OperandType.InlineVar => ReadUInt16(il, ref position),
				OperandType.ShortInlineBrTarget => ReadShortBranchTarget(il, ref position),
				OperandType.InlineBrTarget => ReadBranchTarget(il, ref position),
				OperandType.InlineSwitch => ReadSwitchTargets(il, ref position),
				OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineSig or
				OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType => ReadInt32(il, ref position),
				_ => throw new NotSupportedException($"CIL operand type '{opCode.OperandType}' is not supported")
			};
			result.Add(new CilInstruction(offset, opCode, operand));
		}
		return result;
	}

	private static int ReadInt32(byte[] bytes, ref int position)
	{
		var value = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(position, 4));
		position += 4;
		return value;
	}

	private static long ReadInt64(byte[] bytes, ref int position)
	{
		var value = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(position, 8));
		position += 8;
		return value;
	}

	private static ushort ReadUInt16(byte[] bytes, ref int position)
	{
		var value = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position, 2));
		position += 2;
		return value;
	}

	private static float ReadSingle(byte[] bytes, ref int position)
	{
		var value = BitConverter.Int32BitsToSingle(ReadInt32(bytes, ref position));
		return value;
	}

	private static double ReadDouble(byte[] bytes, ref int position)
	{
		var value = BitConverter.Int64BitsToDouble(ReadInt64(bytes, ref position));
		return value;
	}

	private static int ReadBranchTarget(byte[] bytes, ref int position)
	{
		var delta = ReadInt32(bytes, ref position);
		return position + delta;
	}

	private static int ReadShortBranchTarget(byte[] bytes, ref int position)
	{
		var delta = unchecked((sbyte)bytes[position++]);
		return position + delta;
	}

	private static int[] ReadSwitchTargets(byte[] bytes, ref int position)
	{
		var count = ReadInt32(bytes, ref position);
		var deltas = new int[count];
		for (var i = 0; i < count; i++) deltas[i] = ReadInt32(bytes, ref position);
		var nextInstruction = position;
		for (var i = 0; i < count; i++) deltas[i] += nextInstruction;
		return deltas;
	}
}
