using System.Runtime.InteropServices;
using ClrDebug;

namespace DotnetDbg.Infrastructure.Debugger;

public partial class ManagedDebugger
{
	public string GetValueForCorDebugValue(CorDebugValue corDebugValue)
    {
	    var value = corDebugValue switch
	    {
		    CorDebugBoxValue corDebugBoxValue => throw new NotImplementedException(),
		    CorDebugArrayValue corDebugArrayValue => $"[{corDebugArrayValue.Count}]",
		    CorDebugStringValue stringValue => stringValue.GetString(stringValue.Size),

		    CorDebugContext corDebugContext => throw new NotImplementedException(),
		    CorDebugObjectValue corDebugObjectValue => GetCorDebugObjectValue_Value_AsString(corDebugObjectValue),
		    CorDebugHandleValue corDebugHandleValue => throw new NotImplementedException(),
		    CorDebugReferenceValue corDebugReferenceValue => GetCorDebugReferenceValue_Value_AsString(corDebugReferenceValue),

		    CorDebugHeapValue corDebugHeapValue => throw new NotImplementedException(),
		    CorDebugGenericValue corDebugGenericValue => GetCorDebugGenericValue_Value_AsString(corDebugGenericValue),  // This should be already handled by the above classes, so we should never get here
		    _ => throw new ArgumentOutOfRangeException()
	    };
	    return value;
    }

    public string GetCorDebugObjectValue_Value_AsString(CorDebugObjectValue corDebugObjectValue)
    {
	    var corDebugClass = corDebugObjectValue.ExactType.Class;
	    var module = corDebugClass.Module;
	    var token = corDebugClass.Token;
	    var metadataImport = module.GetMetaDataInterface().MetaDataImport;
	    var typeDefProps = metadataImport.GetTypeDefProps(token);
	    var typeName = typeDefProps.szTypeDef;
	    return $"{{{typeName}}}";
    }

    public string GetCorDebugReferenceValue_Value_AsString(CorDebugReferenceValue corDebugReferenceValue)
    {
	    if (corDebugReferenceValue.IsNull) return "null";
	    var referencedValue = corDebugReferenceValue.Dereference();
	    var value = GetValueForCorDebugValue(referencedValue);
	    return value;
    }

    public string GetCorDebugGenericValue_Value_AsString(CorDebugGenericValue corDebugGenericValue)
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
	        return value;
	    }
	    finally
	    {
	        Marshal.FreeHGlobal(buffer);
	    }
    }
}
