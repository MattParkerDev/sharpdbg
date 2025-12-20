using System.Runtime.InteropServices;
using ClrDebug;

namespace DotnetDbg.Infrastructure.Debugger;

public partial class ManagedDebugger
{
	public (string friendlyTypeName, string value) GetValueForCorDebugValue(CorDebugValue corDebugValue)
    {
	    var (friendlyTypeName, value) = corDebugValue switch
	    {
		    CorDebugBoxValue corDebugBoxValue => GetCorDebugBoxValue_Value_AsString(corDebugBoxValue),
		    CorDebugArrayValue corDebugArrayValue => ("TODO[]", $"[{corDebugArrayValue.Count}]"),
		    CorDebugStringValue stringValue => ("string", stringValue.GetString(stringValue.Size)),

		    CorDebugContext corDebugContext => throw new NotImplementedException(),
		    CorDebugObjectValue corDebugObjectValue => GetCorDebugObjectValue_Value_AsString(corDebugObjectValue),
		    //CorDebugHandleValue corDebugHandleValue => throw new NotImplementedException(), // handled by CorDebugReferenceValue
		    CorDebugReferenceValue corDebugReferenceValue => GetCorDebugReferenceValue_Value_AsString(corDebugReferenceValue),

		    CorDebugHeapValue corDebugHeapValue => throw new NotImplementedException(),
		    CorDebugGenericValue corDebugGenericValue => GetCorDebugGenericValue_Value_AsString(corDebugGenericValue),  // This should be already handled by the above classes, so we should never get here
		    _ => throw new ArgumentOutOfRangeException()
	    };
	    return (friendlyTypeName, value);
    }

	public (string friendlyTypeName, string value) GetCorDebugBoxValue_Value_AsString(CorDebugBoxValue corDebugBoxValue)
	{
	    var boxedValue = corDebugBoxValue.Object;
	    var value = GetValueForCorDebugValue(boxedValue);
	    return value;
	}

    public (string friendlyTypeName, string value) GetCorDebugObjectValue_Value_AsString(CorDebugObjectValue corDebugObjectValue)
    {
	    var typeName = GetCorDebugTypeFriendlyName(corDebugObjectValue.ExactType);
	    return (typeName, $"{{{typeName}}}");
    }

    public (string friendlyTypeName, string value) GetCorDebugReferenceValue_Value_AsString(CorDebugReferenceValue corDebugReferenceValue)
    {
	    //if (corDebugReferenceValue.IsNull) return ("TODO", "null");
	    if (corDebugReferenceValue.IsNull)
	    {
		    // Get the type information even though the reference is null
		    var typeName = GetCorDebugTypeFriendlyName(corDebugReferenceValue.ExactType);
		    return (typeName, "null");
	    }
	    var referencedValue = corDebugReferenceValue.Dereference();
	    var value = GetValueForCorDebugValue(referencedValue);
	    return value;
    }

    private static string GetCorDebugTypeFriendlyName(CorDebugType corDebugType)
    {
	    var primitiveName = GetFriendlyTypeName(corDebugType.Type);
	    if (primitiveName is not null) return primitiveName;
	    var corDebugClass = corDebugType.Class;
	    var module = corDebugClass.Module;
	    var token = corDebugClass.Token;
	    var metadataImport = module.GetMetaDataInterface().MetaDataImport;
	    var typeDefProps = metadataImport.GetTypeDefProps(token);
	    var typeName = typeDefProps.szTypeDef;

	    // Get generic type parameters
	    var genericArgs = new List<string>();
	    var typeParameters = corDebugType.TypeParameters;

	    foreach (var typeParameter in typeParameters)
	    {
		    string argName = GetCorDebugTypeFriendlyName(typeParameter);
		    genericArgs.Add(argName);
	    }

	    // Replace the backtick notation with angle brackets
	    if (genericArgs.Count > 0)
	    {
		    // Remove the `1, `2, etc. from the type name
		    var backtickIndex = typeName.LastIndexOf('`');
		    if (backtickIndex is -1) throw new InvalidOperationException("Generic type name does not contain backtick");
		    typeName = typeName[..backtickIndex];
		    // Add generic arguments
		    typeName = $"{typeName}<{string.Join(", ", genericArgs)}>";
	    }

	    var languageAlias = ClassNameToMaybeLanguageAlias(typeName);
	    return languageAlias;
	}

	private static string ClassNameToMaybeLanguageAlias(string className)
	{
		className = className switch
		{
			"System.String" => "string",
			"System.Object" => "object",
			_ => className
		};
		return className;
	}

    public (string friendlyTypeName, string value) GetCorDebugGenericValue_Value_AsString(CorDebugGenericValue corDebugGenericValue)
    {
	    IntPtr buffer = Marshal.AllocHGlobal(corDebugGenericValue.Size);
	    try
	    {
		    corDebugGenericValue.GetValue(buffer);
	        // Read the value from buffer based on the CorElementType
	        // e.g., for int: Marshal.ReadInt32(buffer)
	        var value = corDebugGenericValue.Type switch
	        {
	            CorElementType.Void => "void",
	            CorElementType.Boolean => Marshal.ReadByte(buffer) != 0 ? "true" : "false",
	            CorElementType.Char => ((char)Marshal.ReadInt16(buffer)).ToString(),
	            CorElementType.I1 => Marshal.ReadByte(buffer).ToString(),
	            CorElementType.I2 => Marshal.ReadInt16(buffer).ToString(),
	            CorElementType.I4 => Marshal.ReadInt32(buffer).ToString(),
	            CorElementType.I8 => Marshal.ReadInt64(buffer).ToString(),
	            CorElementType.U1 => Marshal.ReadByte(buffer).ToString(),
	            CorElementType.U2 => ((ushort)Marshal.ReadInt16(buffer)).ToString(),
	            CorElementType.U4 => ((uint)Marshal.ReadInt32(buffer)).ToString(),
	            CorElementType.U8 => ((ulong)Marshal.ReadInt64(buffer)).ToString(),
	            // Apparently this will blow up on big-endian systems
	            CorElementType.R4 => BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(buffer)), 0).ToString(),
	            CorElementType.R8 => BitConverter.ToDouble(BitConverter.GetBytes(Marshal.ReadInt64(buffer)), 0).ToString(),
	            // native integer
	            CorElementType.I => IntPtr.Size is 4 ? Marshal.ReadInt32(buffer).ToString() : Marshal.ReadInt64(buffer).ToString(),
	            CorElementType.U => IntPtr.Size is 4 ? ((uint)Marshal.ReadInt32(buffer)).ToString() : ((ulong)Marshal.ReadInt64(buffer)).ToString(),
	            CorElementType.R => throw new NotImplementedException(),
	            CorElementType.String => throw new ArgumentOutOfRangeException(), // Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer)) ?? "null",
	            CorElementType.Ptr => throw new ArgumentOutOfRangeException(), // $"0x{Marshal.ReadIntPtr(buffer).ToInt64():X}",
	            CorElementType.ByRef => throw new ArgumentOutOfRangeException(), // $"0x{Marshal.ReadIntPtr(buffer).ToInt64():X}",
	            CorElementType.ValueType => throw new NotImplementedException(),
	            CorElementType.Class => throw new NotImplementedException(),
	            _ => throw new ArgumentOutOfRangeException()
	        };
	        var friendlyTypeName = GetFriendlyTypeName(corDebugGenericValue.Type) ?? throw new ArgumentOutOfRangeException();
	        return (friendlyTypeName, value);
	    }
	    finally
	    {
	        Marshal.FreeHGlobal(buffer);
	    }
    }

    private static string? GetFriendlyTypeName(CorElementType elementType)
    {
	    return elementType switch
	    {
		    CorElementType.Void => "void",
		    CorElementType.Boolean => "bool",
		    CorElementType.Char => "char",
		    CorElementType. I1 => "sbyte",
		    CorElementType.U1 => "byte",
		    CorElementType.I2 => "short",
		    CorElementType.U2 => "ushort",
		    CorElementType.I4 => "int",
		    CorElementType. U4 => "uint",
		    CorElementType.I8 => "long",
		    CorElementType.U8 => "ulong",
		    CorElementType.R4 => "float",
		    CorElementType.R8 => "double",
		    CorElementType.String => "string",
		    CorElementType.Object => "object", // Should we ever see this?
		    CorElementType.I => "nint",
		    CorElementType.U => "nuint",
		    _ => null
	    };
    }
}
