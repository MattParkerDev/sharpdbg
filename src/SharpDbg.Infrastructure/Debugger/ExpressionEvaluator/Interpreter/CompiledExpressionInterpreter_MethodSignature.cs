using System.Reflection.Metadata;
using ClrDebug;

namespace SharpDbg.Infrastructure.Debugger.ExpressionEvaluator.Interpreter;

public partial class CompiledExpressionInterpreter
{
	private static List<TypeInfo> ParseMethodSignatureWithMetadata(IntPtr ppvSigBlob, int pcbSigBlob)
	{
		var parameters = new List<TypeInfo>();

		unsafe
		{
			// Create a BlobReader from the signature blob
			//var blob = new ReadOnlySpan<byte>((void*)ppvSigBlob, pcbSigBlob);
			var reader = new BlobReader((byte*) ppvSigBlob, pcbSigBlob);

			// Decode the method signature
			var header = reader.ReadSignatureHeader();

			// Read generic parameter count if present
			int genericParamCount = 0;
			if (header.IsGeneric)
			{
				genericParamCount = reader.ReadCompressedInteger();
			}

			// Read parameter count
			int paramCount = reader.ReadCompressedInteger();

			// Read return type (skip it)
			DecodeType(ref reader); // Return type

			// Read each parameter type
			for (int i = 0; i < paramCount; i++)
			{
				var typeInfo = DecodeType(ref reader);
				parameters.Add(typeInfo);
			}
		}

		return parameters;
	}

	private class TypeInfo
	{
		public SignatureTypeCode TypeCode { get; set; }
		public int Token { get; set; } // For class/valuetype
		public TypeInfo? ElementType { get; set; } // For arrays, pointers, etc.
		public List<TypeInfo> GenericArguments { get; set; } // For generic types
	}

	private static TypeInfo DecodeType(ref BlobReader reader)
	{
		var typeInfo = new TypeInfo();
		var typeCode = reader.ReadSignatureTypeCode();
		typeInfo.TypeCode = typeCode;

		switch (typeCode)
		{
			case SignatureTypeCode.Boolean:
			case SignatureTypeCode.Char:
			case SignatureTypeCode.SByte:
			case SignatureTypeCode.Byte:
			case SignatureTypeCode.Int16:
			case SignatureTypeCode.UInt16:
			case SignatureTypeCode.Int32:
			case SignatureTypeCode.UInt32:
			case SignatureTypeCode.Int64:
			case SignatureTypeCode.UInt64:
			case SignatureTypeCode.Single:
			case SignatureTypeCode.Double:
			case SignatureTypeCode.IntPtr:
			case SignatureTypeCode.UIntPtr:
			case SignatureTypeCode.Object:
			case SignatureTypeCode.String:
			case SignatureTypeCode.Void:
				// Simple types - no additional data
				break;

			case SignatureTypeCode.TypeHandle:
				// Class or ValueType - read the token
				typeInfo.Token = reader.ReadCompressedInteger();
				break;

			case SignatureTypeCode.SZArray:
			case SignatureTypeCode.Pointer:
			case SignatureTypeCode.ByReference:
				// Element type follows
				typeInfo.ElementType = DecodeType(ref reader);
				break;

			case SignatureTypeCode.GenericTypeInstance:
				// Generic type:  read base type, then argument count, then arguments
				typeInfo.ElementType = DecodeType(ref reader);
				int genericArgCount = reader.ReadCompressedInteger();
				typeInfo.GenericArguments = new List<TypeInfo>();
				for (int i = 0; i < genericArgCount; i++)
				{
					typeInfo.GenericArguments.Add(DecodeType(ref reader));
				}

				break;

			case SignatureTypeCode.GenericTypeParameter:
			case SignatureTypeCode.GenericMethodParameter:
				// Read parameter index
				typeInfo.Token = reader.ReadCompressedInteger();
				break;

			case SignatureTypeCode.Array:
				// Multi-dimensional array
				typeInfo.ElementType = DecodeType(ref reader);
				int rank = reader.ReadCompressedInteger();
				int numSizes = reader.ReadCompressedInteger();
				for (int i = 0; i < numSizes; i++)
					reader.ReadCompressedInteger(); // sizes
				int numLoBounds = reader.ReadCompressedInteger();
				for (int i = 0; i < numLoBounds; i++)
					reader.ReadCompressedSignedInteger(); // lower bounds
				break;
		}

		return typeInfo;
	}

	private static bool IsTypeMatch(TypeInfo paramType, CorElementType argType, CorDebugValue argValue)
	{
		// Map SignatureTypeCode to CorElementType for comparison
		var expectedCorType = SignatureTypeCodeToCorElementType(paramType.TypeCode);

		if (expectedCorType == argType)
			return true;

		// Handle special cases like class types, generic types, etc.
		if (paramType.TypeCode == SignatureTypeCode.TypeHandle)
		{
			// Need to compare actual type tokens or class information
			// You might need to get the class from argValue and compare
			if (argValue.ExactType != null)
			{
				// Compare class tokens if available
				var argClass = argValue.ExactType.Class;
				// Compare paramType.Token with the class token
				// This requires converting the compressed token format
			}
		}

		return false;
	}

	private static CorElementType SignatureTypeCodeToCorElementType(SignatureTypeCode typeCode)
	{
		return typeCode switch
		{
			SignatureTypeCode.Void => CorElementType.Void,
			SignatureTypeCode.Boolean => CorElementType.Boolean,
			SignatureTypeCode.Char => CorElementType.Char,
			SignatureTypeCode.SByte => CorElementType.I1,
			SignatureTypeCode.Byte => CorElementType.U1,
			SignatureTypeCode.Int16 => CorElementType.I2,
			SignatureTypeCode.UInt16 => CorElementType.U2,
			SignatureTypeCode.Int32 => CorElementType.I4,
			SignatureTypeCode.UInt32 => CorElementType.U4,
			SignatureTypeCode.Int64 => CorElementType.I8,
			SignatureTypeCode.UInt64 => CorElementType.U8,
			SignatureTypeCode.Single => CorElementType.R4,
			SignatureTypeCode.Double => CorElementType.R8,
			SignatureTypeCode.String => CorElementType.String,
			SignatureTypeCode.Pointer => CorElementType.Ptr,
			SignatureTypeCode.ByReference => CorElementType.ByRef,
			SignatureTypeCode.TypeHandle => CorElementType.Class, // or ValueType
			SignatureTypeCode.Object => CorElementType.Object,
			SignatureTypeCode.SZArray => CorElementType.SZArray,
			SignatureTypeCode.Array => CorElementType.Array,
			SignatureTypeCode.IntPtr => CorElementType.I,
			SignatureTypeCode.UIntPtr => CorElementType.U,
			SignatureTypeCode.GenericTypeInstance => CorElementType.GenericInst,
			SignatureTypeCode.GenericTypeParameter => CorElementType.Var,
			SignatureTypeCode.GenericMethodParameter => CorElementType.MVar,
			_ => CorElementType.End
		};
	}
}
