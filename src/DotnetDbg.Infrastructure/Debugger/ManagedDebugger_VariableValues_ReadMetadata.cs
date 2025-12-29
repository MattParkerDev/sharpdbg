using System.Reflection.Metadata;
using System.Runtime.InteropServices;
using ClrDebug;

namespace DotnetDbg.Infrastructure.Debugger;

public partial class ManagedDebugger
{
	private static int GetCustomAttributeResultInt(GetCustomAttributeByNameResult attribute)
	{
		var dataIntPtr = attribute.ppData;
		var byteArray = new byte[attribute.pcbData];
		Marshal.Copy(dataIntPtr, byteArray, 0, byteArray.Length);
		// 2 bytes prolog
		// 4 bytes data
		// 2 bytes alignment
		var byteSpan = byteArray.AsSpan()[2..^2];
		var dataAsInt = BitConverter.ToInt32(byteSpan);
		return dataAsInt;
	}

	private static string GetCustomAttributeResultString(GetCustomAttributeByNameResult attribute)
	{
		var dataAsString = GetCustomAttributeCtorStringArg(attribute.ppData, attribute.pcbData); // e.g. "Count = {Count}" or "{DebuggerDisplay,nq}"
		return dataAsString ?? string.Empty;
	}

	private static unsafe string? GetCustomAttributeCtorStringArg(IntPtr ppData, int pcbData)
	{
		var reader = new BlobReader((byte*)ppData, pcbData);

		// 1. Prolog (must be 0x0001)
		ushort prolog = reader.ReadUInt16();
		if (prolog != 0x0001) throw new InvalidOperationException("Invalid custom attribute prolog");

		// 2. Read constructor fixed arguments
		// DebuggerDisplay has one string ctor arg
		var stringCtorArg = reader.ReadSerializedString();
		return stringCtorArg;
	}
	private static (string, string?) GetCustomAttributeCtorStringArgAndNamedArg(GetCustomAttributeByNameResult customAttributeByNameResult, string namedArgumentName) => GetCustomAttributeCtorStringArgAndNamedArg(customAttributeByNameResult.ppData, customAttributeByNameResult.pcbData, namedArgumentName)!;

	private static unsafe (string?, string?) GetCustomAttributeCtorStringArgAndNamedArg(IntPtr ppData, int pcbData, string namedArgumentName)
	{
		var reader = new BlobReader((byte*) ppData, pcbData);

		// 1. Prolog (must be 0x0001)
		ushort prolog = reader.ReadUInt16();
		if (prolog != 0x0001) throw new InvalidOperationException("Invalid custom attribute prolog");

		// 2. Read constructor fixed arguments
		// DebuggerDisplay has one string ctor arg
		var stringCtorArg = reader.ReadSerializedString();

		// 3. Number of named arguments
		ushort namedCount = reader.ReadUInt16();

		// 4. Parse named arguments
		for (var i = 0; i < namedCount; i++)
		{
			byte fieldOrProp = reader.ReadByte(); // 0x53 or 0x54
			var type = reader.ReadSignatureTypeCode();
			string name = reader.ReadSerializedString()!;

			// We assume the named argument is a string, update or make new method if needed
			var value = reader.ReadSerializedString();

			if (name == namedArgumentName)
			{
				return (stringCtorArg, value);
			}
		}

		return (stringCtorArg, null);
	}
}
