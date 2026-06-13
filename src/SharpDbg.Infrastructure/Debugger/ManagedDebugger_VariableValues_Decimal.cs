using System.Runtime.InteropServices;
using ClrDebug;

namespace SharpDbg.Infrastructure.Debugger;

public partial class ManagedDebugger
{
	private static string GetDecimalValueString(CorDebugObjectValue corDebugObjectValue)
	{
		var module = corDebugObjectValue.Class.Module;
		var metaDataImport = module.GetMetaDataInterface().MetaDataImport;
		var classToken = corDebugObjectValue.Class.Token;

		uint lo = 0, mid = 0, hi = 0, flags = 0;
		bool hasLo = false, hasMid = false, hasHi = false, hasFlags = false;

		foreach (var fieldDef in metaDataImport.EnumFields(classToken))
		{
			var fieldProps = metaDataImport.GetFieldProps(fieldDef);
			var name = fieldProps.szField;
			if (name is null) continue;

			uint ReadUInt32Field()
			{
				var fieldValue = corDebugObjectValue.GetFieldValue(corDebugObjectValue.Class.Raw, fieldDef);
				IntPtr buffer = Marshal.AllocHGlobal(4);
				try
				{
					((CorDebugGenericValue) fieldValue.UnwrapDebugValue()).GetValue(buffer);
					return (uint) Marshal.ReadInt32(buffer);
				}
				finally
				{
					Marshal.FreeHGlobal(buffer);
				}
			}

			switch (name)
			{
				case "lo" or "_lo32":
					lo = ReadUInt32Field();
					hasLo = true;
					break;
				case "mid":
					mid = ReadUInt32Field();
					hasMid = true;
					break;
				case "hi" or "_hi32":
					hi = ReadUInt32Field();
					hasHi = true;
					break;
				case "flags" or "_flags":
					flags = ReadUInt32Field();
					hasFlags = true;
					break;
				case "_lo64":
					// .NET 7+ layout: _lo64 is a ulong covering lo+mid
					var fieldValue64 = corDebugObjectValue.GetFieldValue(corDebugObjectValue.Class.Raw, fieldDef);
					IntPtr buf64 = Marshal.AllocHGlobal(8);
					try
					{
						((CorDebugGenericValue)fieldValue64.UnwrapDebugValue()).GetValue(buf64);
						var raw64 = (ulong) Marshal.ReadInt64(buf64);
						lo = (uint) (raw64 & 0xFFFFFFFF);
						mid = (uint) (raw64 >> 32);
					}
					finally
					{
						Marshal.FreeHGlobal(buf64);
					}

					hasLo = true;
					hasMid = true;
					break;
			}
		}

		if (!hasLo || !hasMid || !hasHi || !hasFlags) return "{decimal}"; // fallback if layout is unrecognised

		// Reconstruct the decimal string from the 96-bit integer + scale + sign
		// The 96-bit mantissa is hi:mid:lo (big-endian 32-bit words)
		uint[] words = [lo, mid, hi]; // little-endian words for the big-integer arithmetic
		var digits = UInt96ToDecimalDigits(words);

		uint scale = (flags >> 16) & 0xFF;
		bool negative = (flags & 0x80000000u) != 0;

		// Insert decimal point
		if (scale > 0)
		{
			if (digits.Length > (int) scale)
			{
				digits = digits.Insert(digits.Length - (int) scale, ".");
			}
			else
			{
				// e.g. scale=3, digits="5" → "0.005"
				digits = "0." + digits.PadLeft((int) scale, '0');
			}
		}

		return negative ? $"-{digits}" : digits;
	}

	/// Converts a 96-bit unsigned integer (stored as three little-endian uint32 words: [lo, mid, hi])
	/// to its base-10 string representation.
	private static string UInt96ToDecimalDigits(uint[] words)
	{
		// Repeated division by 10 using 32-bit limbs
		var limbs = (uint[])words.Clone();
		var result = new System.Text.StringBuilder();
		while (limbs[0] != 0 || limbs[1] != 0 || limbs[2] != 0)
		{
			ulong carry = 0;
			for (int i = 2; i >= 0; i--)
			{
				ulong cur = carry * 0x100000000UL + limbs[i];
				limbs[i] = (uint)(cur / 10);
				carry = cur % 10;
			}
			result.Insert(0, (char)('0' + carry));
		}
		return result.Length == 0 ? "0" : result.ToString();
	}
}
