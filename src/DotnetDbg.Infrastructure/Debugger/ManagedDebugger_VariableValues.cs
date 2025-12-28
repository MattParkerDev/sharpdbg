using System.Runtime.InteropServices;
using System.Text;
using ClrDebug;
using DotnetDbg.Infrastructure.Debugger.ExpressionEvaluator;
using DotnetDbg.Infrastructure.Debugger.ExpressionEvaluator.Compiler;

namespace DotnetDbg.Infrastructure.Debugger;

public partial class ManagedDebugger
{
	public async Task<(string friendlyTypeName, string value)> GetValueForCorDebugValueAsync(CorDebugValue corDebugValue, ThreadId threadId, FrameStackDepth frameStackDepth)
	{
		var (friendlyTypeName, value, valueRequiresDebuggerDisplayEval) = GetValueForCorDebugValue(corDebugValue);
		if (valueRequiresDebuggerDisplayEval)
		{
			var compiledExpression = ExpressionCompiler.Compile($"$\"{value}\"", true);
			var thread = _process!.GetThread(threadId.Value);
			var evalContext = new CompiledExpressionEvaluationContext(thread, threadId, frameStackDepth, corDebugValue);
			var result = await _expressionInterpreter!.Interpret(compiledExpression, evalContext);
			if (result.Error is not null)
			{
				_logger?.Invoke($"Evaluation error: {result.Error}");
				return (friendlyTypeName, result.Error);
			}
			(_, value, _) = GetValueForCorDebugValue(result.Value!);
		}
		return (friendlyTypeName, value);
	}

	private static (string friendlyTypeName, string value, bool valueRequiresDebuggerDisplayEval) GetValueForCorDebugValue(CorDebugValue corDebugValue)
    {
	    var (friendlyTypeName, value, valueRequiresDebuggerDisplayEval) = corDebugValue switch
	    {
		    CorDebugBoxValue corDebugBoxValue => GetCorDebugBoxValue_Value_AsString(corDebugBoxValue),
		    CorDebugArrayValue corDebugArrayValue => Get_CorDebugArrayValue_AsString(corDebugArrayValue),
		    CorDebugStringValue stringValue => Get_CorDebugStringValue_AsString(stringValue),

		    CorDebugContext corDebugContext => throw new NotImplementedException(),
		    CorDebugObjectValue corDebugObjectValue => GetCorDebugObjectValue_Value_AsString(corDebugObjectValue),
		    //CorDebugHandleValue corDebugHandleValue => throw new NotImplementedException(), // handled by CorDebugReferenceValue
		    CorDebugReferenceValue corDebugReferenceValue => GetCorDebugReferenceValue_Value_AsString(corDebugReferenceValue),

		    CorDebugHeapValue corDebugHeapValue => throw new NotImplementedException(),
		    CorDebugGenericValue corDebugGenericValue => GetCorDebugGenericValue_Value_AsString(corDebugGenericValue),  // This should be already handled by the above classes, so we should never get here
		    _ => throw new ArgumentOutOfRangeException(nameof(corDebugValue))
	    };
	    return (friendlyTypeName, value, valueRequiresDebuggerDisplayEval);
    }

	private static (string friendlyTypeName, string value, bool valueRequiresDebuggerDisplayEval) Get_CorDebugStringValue_AsString(CorDebugStringValue corDebugStringValue)
	{
		var text = corDebugStringValue.GetStringWithoutBug(corDebugStringValue.Length + 1);
		return ("string", text, false);
	}

	public static (string friendlyTypeName, string value, bool valueRequiresDebuggerDisplayEval) Get_CorDebugArrayValue_AsString(CorDebugArrayValue corDebugArrayValue)
	{
		var elementName = GetFriendlyTypeName(corDebugArrayValue.ElementType);
		var typeName = $"{elementName}[]";
	    return (typeName, $"{elementName}[{corDebugArrayValue.Count}]", false);
	}

	public static (string friendlyTypeName, string value, bool valueRequiresDebuggerDisplayEval) GetCorDebugBoxValue_Value_AsString(CorDebugBoxValue corDebugBoxValue)
	{
	    var unboxedValue = corDebugBoxValue.Object;
	    var value = GetValueForCorDebugValue(unboxedValue);
	    return value;
	}

    public static (string friendlyTypeName, string value, bool valueRequiresDebuggerDisplayEval) GetCorDebugObjectValue_Value_AsString(CorDebugObjectValue corDebugObjectValue)
    {
	    var module = corDebugObjectValue.Class.Module;
	    var metaDataImport = module.GetMetaDataInterface().MetaDataImport;
	    var baseTypeName = GetCorDebugTypeFriendlyName(corDebugObjectValue.ExactType.Base);
	    if (baseTypeName == "System.Enum")
	    {
			var valueFieldDef = metaDataImport.FindField(corDebugObjectValue.Class.Token, "value__", 0, 0);
			var valueField = corDebugObjectValue.GetFieldValue(corDebugObjectValue.Class.Raw, valueFieldDef);
			var value = GetValueForCorDebugValue(valueField);

			var enumDisplayValue = GetEnumDisplayValue(metaDataImport, corDebugObjectValue, value.value);
			return (GetCorDebugTypeFriendlyName(corDebugObjectValue.ExactType), enumDisplayValue, false);
	    }
	    var typeName = GetCorDebugTypeFriendlyName(corDebugObjectValue.ExactType);
	    if (typeName.EndsWith('?'))
	    {
		    var underlyingValueOrNull = GetUnderlyingValueOrNullFromNullableStruct(corDebugObjectValue);
		    if (underlyingValueOrNull is null) return (typeName, "null", false);
		    var value = GetValueForCorDebugValue(underlyingValueOrNull);
		    return (typeName, value.value, false);
	    }
		var hasDebuggerTypeProxyAttribute = metaDataImport.TryGetCustomAttributeByName(corDebugObjectValue.Class.Token, "System.Diagnostics.DebuggerTypeProxyAttribute", out _) is HRESULT.S_OK;
		var hasDebuggerDisplayAttribute = metaDataImport.TryGetCustomAttributeByName(corDebugObjectValue.Class.Token, "System.Diagnostics.DebuggerDisplayAttribute", out var debuggerDisplayAttribute) is HRESULT.S_OK;
		if (hasDebuggerDisplayAttribute)
		{
			var debuggerDisplayValue = GetUnevaluatedDebuggerDisplayString(debuggerDisplayAttribute, corDebugObjectValue);
			return (typeName, debuggerDisplayValue, true);
		}

	    return (typeName, $"{{{typeName}}}", false);
    }

    private static string GetUnevaluatedDebuggerDisplayString(GetCustomAttributeByNameResult attribute, CorDebugObjectValue corDebugObjectValue)
    {
	    var dataIntPtr = attribute.ppData;
	    var byteArray = new byte[attribute.pcbData];
	    Marshal.Copy(dataIntPtr, byteArray, 0, byteArray.Length);
	    // Marshal.PtrToStringUTF8(dataIntPtr, attribute.pcbData) returns "[SOH][NUL][SI]Count = {Count}[NUL][NUL]"
	    // The first 3 characters are control characters, then the string, then two NUL characters
	    var byteSpan = byteArray.AsSpan()[3..^2];
	    var dataAsString = Encoding.UTF8.GetString(byteSpan); // e.g. "Count = {Count}" or "{DebuggerDisplay,nq}"
	    // Now we need to parse the string and replace {Count} with the actual value, or just eval the expression
	    return dataAsString;
	}

    private static CorDebugValue? GetUnderlyingValueOrNullFromNullableStruct(CorDebugObjectValue corDebugObjectValue)
	{
	    var module = corDebugObjectValue.Class.Module;
	    var metaDataImport = module.GetMetaDataInterface().MetaDataImport;
	    var hasValueFieldDef = metaDataImport.FindField(corDebugObjectValue.Class.Token, "hasValue", 0, 0);
	    var valueFieldDef = metaDataImport.FindField(corDebugObjectValue.Class.Token, "value", 0, 0);

	    var hasValueDebugObjectValue = corDebugObjectValue.GetFieldValue(corDebugObjectValue.Class.Raw, hasValueFieldDef);
	    var hasValueValue = GetValueForCorDebugValue(hasValueDebugObjectValue);
	    if (hasValueValue.value is "false") return null;
	    var valueValue = corDebugObjectValue.GetFieldValue(corDebugObjectValue.Class.Raw, valueFieldDef);
	    return valueValue;
	}

    public static (string friendlyTypeName, string value, bool valueRequiresDebuggerDisplayEval) GetCorDebugReferenceValue_Value_AsString(CorDebugReferenceValue corDebugReferenceValue)
    {
	    //if (corDebugReferenceValue.IsNull) return ("TODO", "null");
	    if (corDebugReferenceValue.IsNull)
	    {
		    // Get the type information even though the reference is null
		    var typeName = GetCorDebugTypeFriendlyName(corDebugReferenceValue.ExactType);
		    return (typeName, "null", false);
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

	    if (typeName.StartsWith("System.Nullable<")) // unwrap System.Nullable<int> to int?
	    {
		    var span = typeName.AsSpan();
		    var openingIndex = span.IndexOf('<');
		    var closingIndex = span.LastIndexOf('>');
		    var underlyingType = span.Slice(openingIndex + 1, closingIndex - openingIndex - 1);
		    typeName = $"{underlyingType}?";
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

    public static (string friendlyTypeName, string value, bool valueRequiresDebuggerDisplayEval) GetCorDebugGenericValue_Value_AsString(CorDebugGenericValue corDebugGenericValue)
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
	        return (friendlyTypeName, value, false);
	    }
	    finally
	    {
	        Marshal.FreeHGlobal(buffer);
	    }
    }

    private static object GetLiteralValue(IntPtr ppValue, CorElementType elementType)
    {
	    if (ppValue == IntPtr.Zero) throw new ArgumentNullException(nameof(ppValue));

	    object? result = elementType switch
	    {
		    CorElementType.I1 => Marshal.ReadByte(ppValue),
		    CorElementType.I2 => Marshal.ReadInt16(ppValue),
		    CorElementType.I4 => Marshal.ReadInt32(ppValue),
		    CorElementType.I8 => Marshal.ReadInt64(ppValue),
		    CorElementType.U1 => Marshal.ReadByte(ppValue),
		    CorElementType.U2 => (ushort)Marshal.ReadInt16(ppValue),
		    CorElementType.U4 => (uint)Marshal.ReadInt32(ppValue),
		    CorElementType.U8 => (ulong)Marshal.ReadInt64(ppValue),
		    _ => throw new ArgumentOutOfRangeException(nameof(elementType), $"Unsupported literal type: {elementType}"),
	    };
	    return result;
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
