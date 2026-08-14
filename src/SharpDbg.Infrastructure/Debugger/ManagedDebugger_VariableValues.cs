using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;
using Ardalis.GuardClauses;
using ICorDebugSharp;
using Microsoft.CodeAnalysis.CSharp;
using SharpDbg.Infrastructure.Debugger.ExpressionEvaluator;
using SharpDbg.Infrastructure.Debugger.ExpressionEvaluator.Compiler;

namespace SharpDbg.Infrastructure.Debugger;

public readonly record struct CorDebugValueValueResult(string FriendlyTypeName, string Value, bool ValueRequiresDebuggerDisplayEval, string? DebuggerProxyTypeName);
public partial class ManagedDebugger
{
	public async Task<(string friendlyTypeName, string value, ICorDebugValue? debuggerProxyInstance, bool resultIsError)> GetValueForCorDebugValueAsync(ICorDebugValue corDebugValue, ThreadId threadId, FrameStackDepth frameStackDepth, bool escapeStringValue)
	{
		Guard.Against.Null(corDebugValue);
		var (friendlyTypeName, value, valueRequiresDebuggerDisplayEval, debuggerProxyTypeName) = GetValueForCorDebugValue(corDebugValue, escapeStringValue);
		if (valueRequiresDebuggerDisplayEval)
		{
			var expressionString = $"$\"{value}\"";
			var thread = _process!.GetThread(threadId.Value);
			var evalContext = new CompiledExpressionEvaluationContext(thread, threadId, frameStackDepth, corDebugValue);
			using var result = await _expressionEvaluator!.Evaluate(expressionString, evalContext);
			if (result.Error is not null)
			{
				_logger?.Invoke($"Evaluation error: {result.Error}");
				return (friendlyTypeName, result.Error, null, true);
			}
			(_, value, _, _) = GetValueForCorDebugValue(result.Value!, false);
		}
		ICorDebugValue? proxyInstance = null;
		if (debuggerProxyTypeName is not null)
		{
			var thread = _process!.GetThread(threadId.Value);
			var eval = thread.CreateEval();
			var module = corDebugValue.ExactType.Class.Module;
			var metadataImport = module.GetMetaDataInterface<IMetaDataImport>();
			var debugProxyCorDebugTypeDef = metadataImport.FindMaybeNestedTypeDefByNameOrNull(debuggerProxyTypeName);
			ArgumentNullException.ThrowIfNull(debugProxyCorDebugTypeDef);
			var debugProxyCorDebugClass = module.GetClassFromToken(debugProxyCorDebugTypeDef.Value);

			// TODO: pass a specific signature to handle proxy types that have multiple constructors - see ManagedDebugger.FindMethodOnType
			var debugProxyTypeConstructorMethodDef = metadataImport.FindMethod(debugProxyCorDebugClass.Token, ".ctor", 0, 0);
			//var debugProxyTypeCtorMethodProps = metadataImport.GetMethodProps(debugProxyTypeConstructorMethodDef);
			var corDebugFunction = module.GetFunctionFromToken(debugProxyTypeConstructorMethodDef);
			ICorDebugValue[] evalArgs = [corDebugValue];
			var typeParameterArgs = corDebugValue.ExactType.TypeParameters;
			proxyInstance = await eval.NewParameterizedObjectAsync(ProcessRuntimeEventsUntilEvalEvent, EvalStatus, corDebugFunction, typeParameterArgs.Length, typeParameterArgs, evalArgs.Length, evalArgs);
			ArgumentNullException.ThrowIfNull(proxyInstance);
		}
		return (friendlyTypeName, value, proxyInstance, false);
	}

	private static CorDebugValueValueResult GetValueForCorDebugValue(ICorDebugValue corDebugValue, bool escapeStringValue)
	{
		var (friendlyTypeName, value, valueRequiresDebuggerDisplayEval, debuggerTypeProxy) = corDebugValue switch
		{
			ICorDebugBoxValue corDebugBoxValue => GetCorDebugBoxValue_Value_AsString(corDebugBoxValue, escapeStringValue),
			ICorDebugArrayValue corDebugArrayValue => Get_CorDebugArrayValue_AsString(corDebugArrayValue),
			ICorDebugStringValue stringValue => Get_CorDebugStringValue_AsString(stringValue, escapeStringValue),

			ICorDebugContext corDebugContext => throw new NotImplementedException(),
			ICorDebugObjectValue corDebugObjectValue => GetCorDebugObjectValue_Value_AsString(corDebugObjectValue, escapeStringValue),
			//CorDebugHandleValue corDebugHandleValue => throw new NotImplementedException(), // handled by CorDebugReferenceValue
			ICorDebugReferenceValue corDebugReferenceValue => GetCorDebugReferenceValue_Value_AsString(corDebugReferenceValue, escapeStringValue),

			ICorDebugHeapValue corDebugHeapValue => throw new NotImplementedException(),
			ICorDebugGenericValue corDebugGenericValue => GetCorDebugGenericValue_Value_AsString(corDebugGenericValue),  // This should be already handled by the above classes, so we should never get here
			_ => throw new ArgumentOutOfRangeException(nameof(corDebugValue))
		};
		return new(friendlyTypeName, value, valueRequiresDebuggerDisplayEval, debuggerTypeProxy);
	}

	private static CorDebugValueValueResult Get_CorDebugStringValue_AsString(ICorDebugStringValue corDebugStringValue, bool escapeStringValue)
	{
		var text = corDebugStringValue.String;
		if (escapeStringValue) text = SymbolDisplay.FormatLiteral(text, quote: true);
		return new("string", text, false, null);
	}

	public static CorDebugValueValueResult Get_CorDebugArrayValue_AsString(ICorDebugArrayValue corDebugArrayValue)
	{
		var typeName = GetCorDebugTypeFriendlyName(corDebugArrayValue.ExactType);
		var typeNameSpan = typeName.AsSpan();
		var elementTypeName = typeNameSpan[..typeNameSpan.LastIndexOf('[')];
		var dimensions = corDebugArrayValue.GetDimensions(corDebugArrayValue.Rank);
		var value = $"{elementTypeName}[{string.Join(", ", dimensions)}]";
		return new(typeName, value, false, null);
	}

	public static CorDebugValueValueResult GetCorDebugBoxValue_Value_AsString(ICorDebugBoxValue corDebugBoxValue, bool escapeStringValue)
	{
		var unboxedValue = corDebugBoxValue.Object;
		var value = GetValueForCorDebugValue(unboxedValue, escapeStringValue);
		return value;
	}

	public static CorDebugValueValueResult GetCorDebugObjectValue_Value_AsString(ICorDebugObjectValue corDebugObjectValue, bool escapeStringValue)
	{
		var module = corDebugObjectValue.Class.Module;
		var metaDataImport = module.GetMetaDataInterface<IMetaDataImport>();
		var baseTypeName = corDebugObjectValue.ExactType.Base is {} baseType ? GetCorDebugTypeFriendlyName(baseType) : null; // ExactType.Base is null when ExactType is System.Object
		if (baseTypeName is "System.Enum")
		{
			var valueFieldDef = metaDataImport.FindField(corDebugObjectValue.Class.Token, "value__", 0, 0);
			var valueField = corDebugObjectValue.GetFieldValue(corDebugObjectValue.Class, valueFieldDef);
			var value = GetValueForCorDebugValue(valueField, escapeStringValue);

			var enumDisplayValue = GetEnumDisplayValue(metaDataImport, corDebugObjectValue.Class.Token, value.Value);
			return new(GetCorDebugTypeFriendlyName(corDebugObjectValue.ExactType), enumDisplayValue, false, null);
		}
		var typeName = GetCorDebugTypeFriendlyName(corDebugObjectValue.ExactType);
		if (typeName.EndsWith('?'))
		{
			var underlyingValueOrNull = GetUnderlyingValueOrNullFromNullableStruct(corDebugObjectValue);
			if (underlyingValueOrNull is null) return new(typeName, "null", false, null);
			var value = GetValueForCorDebugValue(underlyingValueOrNull, escapeStringValue);
			return value with { FriendlyTypeName = typeName };
		}
		var hasDebuggerTypeProxyAttribute = metaDataImport.TryGetCustomAttributeByName(corDebugObjectValue.Class.Token, "System.Diagnostics.DebuggerTypeProxyAttribute", out var debuggerTypeProxyAttributePointer, out var debuggerTypeProxyAttributeSize) is Cor.S_OK;
		var hasDebuggerDisplayAttribute = metaDataImport.TryGetCustomAttributeByName(corDebugObjectValue.Class.Token, "System.Diagnostics.DebuggerDisplayAttribute", out var debuggerDisplayAttributePointer, out var debuggerDisplayAttributeSize) is Cor.S_OK;

		var debugProxyTypeName = hasDebuggerTypeProxyAttribute ? GetCustomAttributeResultString(debuggerTypeProxyAttributePointer, debuggerTypeProxyAttributeSize) : null;
		if (hasDebuggerDisplayAttribute)
		{
			var (debuggerDisplayValue, debuggerDisplayName) = GetCustomAttributeCtorStringArgAndNamedArg(debuggerDisplayAttributePointer, debuggerDisplayAttributeSize, "Name");
			if (typeName.StartsWith("<>f__AnonymousType"))
			{
				// DebuggerDisplay Name for an anonymous type is e.g. `\{ Id = {Id}, Name = {Name} }`
				// '\' denotes escaping a bracket for presumably VS's DebuggerDisplay interpreter
				// Since we are leaning on the similarity of DebuggerDisplay strings to interpolated strings, we need to fix the invalid C# syntax before returning it
				// e.g. fixed - `{{ Id = {Id}, Name = {Name} }}`
				debuggerDisplayValue = $$$"""{{{{{debuggerDisplayValue[2..^1]}}}}}"""; // range indexing removes the leading '\{' and trailing '}', which we replace
			}
			// I prefer how Rider handles this - instead of overriding the actual name of the variable, just prefix the value with the name
			if (debuggerDisplayName is not null) debuggerDisplayValue = $"{debuggerDisplayName} = {debuggerDisplayValue}";
			return new(typeName, debuggerDisplayValue, true, debugProxyTypeName);
		}
		if (corDebugObjectValue.ExactType.IsExceptionType())
		{
			return new(typeName, "{ToString()}", true, debugProxyTypeName);
		}
		if (typeName == "decimal")
		{
			// This technically isn't necessary - System.Decimal overrides ToString, which we call below. This might technically be faster? This is how it is implemented in netcoredbg, but they don't handle overridden ToString's
			var decimalString = GetDecimalValueString(corDebugObjectValue);
			return new(typeName, decimalString, false, null);
		}
		if (TypeOverridesToString(corDebugObjectValue.ExactType))
		{
			return new(typeName, "{ToString()}", true, debugProxyTypeName);
		}

		return new(typeName, $"{{{typeName}}}", false, debugProxyTypeName);
	}

	/// Returns true if <paramref name="corDebugType"/> or any of its base types (up to but not
	/// including System.Object / System.ValueType) declares a no-arg "ToString" method directly on itself.
	private static bool TypeOverridesToString(ICorDebugType corDebugType)
	{
		var type = corDebugType;
		while (type is not null)
		{
			var cls = type.Class;
			var module = cls.Module;
			var metaDataImport = module.GetMetaDataInterface<IMetaDataImport>();
			var typeName = metaDataImport.GetTypeDefProps(cls.Token).szTypeDef;
			if (typeName is "System.Object" or "System.ValueType") return false;

			foreach (var methodToken in metaDataImport.EnumMethods(cls.Token))
			{
				var methodProps = metaDataImport.GetMethodProps(methodToken);
				var methodAttr = methodProps.pdwAttr;
				if (methodProps.szMethod is "ToString" && methodAttr.IsMdStatic() is false && methodAttr.IsMdVirtual() && methodAttr.IsMdNewSlot() is false && Marshal.ReadByte(methodProps.ppvSigBlob, 1) is var parameterCount && parameterCount is 0)
					return true;
			}
			type = type.Base;
		}
		return false;
	}

	private static ICorDebugValue? GetUnderlyingValueOrNullFromNullableStruct(ICorDebugObjectValue corDebugObjectValue)
	{
		var module = corDebugObjectValue.Class.Module;
		var metaDataImport = module.GetMetaDataInterface<IMetaDataImport>();
		var hasValueFieldDef = metaDataImport.FindField(corDebugObjectValue.Class.Token, "hasValue", 0, 0);
		var valueFieldDef = metaDataImport.FindField(corDebugObjectValue.Class.Token, "value", 0, 0);

		var hasValueDebugObjectValue = corDebugObjectValue.GetFieldValue(corDebugObjectValue.Class, hasValueFieldDef);
		var hasValueValue = GetValueForCorDebugValue(hasValueDebugObjectValue, false);
		if (hasValueValue.Value is "false") return null;
		var valueValue = corDebugObjectValue.GetFieldValue(corDebugObjectValue.Class, valueFieldDef);
		return valueValue;
	}

	public static CorDebugValueValueResult GetCorDebugReferenceValue_Value_AsString(ICorDebugReferenceValue corDebugReferenceValue, bool escapeStringValue)
	{
		if (corDebugReferenceValue.IsNull)
		{
			// Get the type information even though the reference is null
			var typeName = GetCorDebugTypeFriendlyName(corDebugReferenceValue.ExactType);
			return new(typeName, "null", false, null);
		}
		var referencedValue = corDebugReferenceValue.Dereference();
		var value = GetValueForCorDebugValue(referencedValue, escapeStringValue);
		return value;
	}

	internal static string GetCorDebugTypeFriendlyName(ICorDebugType corDebugType)
	{
		var primitiveName = GetFriendlyTypeName(corDebugType.Type);
		if (primitiveName is not null) return primitiveName;
		if (corDebugType.Type is CorElementType.SZARRAY)
		{
			var elementName = GetCorDebugTypeFriendlyName(corDebugType.FirstTypeParameter);
			return $"{elementName}[]";
		}
		if (corDebugType.Type is CorElementType.ARRAY)
		{
			var elementName = GetCorDebugTypeFriendlyName(corDebugType.FirstTypeParameter);
			return $"{elementName}[{new string(',', corDebugType.Rank - 1)}]";
		}
		var corDebugClass = corDebugType.Class;
		// The specific CorDebugType may have type parameters, but they could be for its enclosing type (e.g. a class defined inside a generic class)
		// So we get them here, and pass it into the recursive GetCorDebugTypeFriendlyNameInternal. Starting from the bottom (highest enclosing type), each level will consume the type parameters it needs, based on its arity, indicated in the name (`1, `2, etc.)
		// e.g. for MyClassContainingAnotherClass<string, int>.MyNestedClass<long, float>, type parameters contains [string, int, long, float]
		var typeParameters = corDebugType.TypeParameters.ToList();
		var name = GetCorDebugTypeFriendlyNameInternal(corDebugClass, typeParameters);
		return name;
	}

	private static string GetCorDebugTypeFriendlyNameInternal(ICorDebugClass corDebugClass, List<ICorDebugType> typeParameterTypes)
	{
		var module = corDebugClass.Module;
		var token = corDebugClass.Token;
		var metadataImport = module.GetMetaDataInterface<IMetaDataImport>();
		var typeDefProps = metadataImport.GetTypeDefProps(token);
		var typeName = typeDefProps.szTypeDef;
		var isNested = typeDefProps.pdwTypeDefFlags.IsTdNested();

		string? parentTypeName = null;
		if (isNested)
		{
			var parentTypeDef = metadataImport.GetNestedClassProps(token);
			var parentTypeCorDebugClass = module.GetClassFromToken(parentTypeDef);
			parentTypeName = GetCorDebugTypeFriendlyNameInternal(parentTypeCorDebugClass, typeParameterTypes);
		}

		// This will be first reached by the outermost type
		// The below will consume type parameters it requires based on arity

		var backtickIndex = typeName.LastIndexOf('`');
		var typeHasTypeParameters = backtickIndex is not -1;
		if (typeHasTypeParameters)
		{
			var typeNameAsSpan = typeName.AsSpan();
			var aritySpan = typeNameAsSpan[(backtickIndex + 1)..];
			if (int.TryParse(aritySpan, out var arity) is false) throw new InvalidOperationException("Failed to parse generic type arity from type name");
			var typeParametersFriendlyNamesForType = typeParameterTypes.Take(arity).Select(GetCorDebugTypeFriendlyName).ToImmutableArray();
			typeParameterTypes.RemoveRange(0, arity);
			typeName = $"{typeName[..backtickIndex]}<{string.Join(", ", typeParametersFriendlyNamesForType)}>";
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
		return isNested ? $"{parentTypeName}.{languageAlias}" : languageAlias;
	}

	private static string ClassNameToMaybeLanguageAlias(string className)
	{
		className = className switch
		{
			"System.String" => "string",
			"System.Object" => "object",
			"System.Decimal" => "decimal",

			// These will be hit in the case that a primitive is boxed, e.g. object myInt = 4;
			"System.Boolean" => "bool",
			"System.Byte" => "byte",
			"System.SByte" => "sbyte",
			"System.Char" => "char",
			"System.Int16" => "short",
			"System.UInt16" => "ushort",
			"System.Int32" => "int",
			"System.UInt32" => "uint",
			"System.Int64" => "long",
			"System.UInt64" => "ulong",
			"System.Single" => "float",
			"System.Double" => "double",
			"System.IntPtr" => "nint",
			"System.UIntPtr" => "nuint",

			_ => className
		};
		return className;
	}

	public static CorDebugValueValueResult GetCorDebugGenericValue_Value_AsString(ICorDebugGenericValue corDebugGenericValue)
	{
		IntPtr buffer = Marshal.AllocHGlobal(corDebugGenericValue.Size);
		try
		{
			corDebugGenericValue.GetValue(buffer);
			// Read the value from buffer based on the CorElementType
			// e.g., for int: Marshal.ReadInt32(buffer)
			var value = corDebugGenericValue.Type switch
			{
				CorElementType.VOID => "void",
				CorElementType.BOOLEAN => Marshal.ReadByte(buffer) != 0 ? "true" : "false",
				CorElementType.CHAR => Marshal.ReadInt16(buffer) is var v ? $"{v} '{(char)v}'" : throw new UnreachableException(),
				CorElementType.I1 => ((sbyte)Marshal.ReadByte(buffer)).ToString(),
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
				CorElementType.STRING => throw new ArgumentOutOfRangeException(), // Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer)) ?? "null",
				CorElementType.PTR => throw new ArgumentOutOfRangeException(), // $"0x{Marshal.ReadIntPtr(buffer).ToInt64():X}",
				CorElementType.BYREF => throw new ArgumentOutOfRangeException(), // $"0x{Marshal.ReadIntPtr(buffer).ToInt64():X}",
				CorElementType.VALUETYPE => throw new NotImplementedException(),
				CorElementType.CLASS => throw new NotImplementedException(),
				_ => throw new ArgumentOutOfRangeException()
			};
			var friendlyTypeName = GetFriendlyTypeName(corDebugGenericValue.Type) ?? throw new ArgumentOutOfRangeException();
			return new(friendlyTypeName, value, false, null);
		}
		finally
		{
			Marshal.FreeHGlobal(buffer);
		}
	}

	private static object? GetLiteralValue(IntPtr ppValue, CorElementType elementType, int pcchValue)
	{
		if (ppValue == IntPtr.Zero) return elementType is CorElementType.STRING or CorElementType.CLASS or CorElementType.OBJECT or CorElementType.VOID ? null : throw new ArgumentNullException(nameof(ppValue));

		object? result = elementType switch
		{
			CorElementType.BOOLEAN => Marshal.ReadByte(ppValue) != 0,
			CorElementType.CHAR => (char)Marshal.ReadInt16(ppValue),
			CorElementType.I1 => (sbyte)Marshal.ReadByte(ppValue),
			CorElementType.I2 => Marshal.ReadInt16(ppValue),
			CorElementType.I4 => Marshal.ReadInt32(ppValue),
			CorElementType.I8 => Marshal.ReadInt64(ppValue),
			CorElementType.U1 => Marshal.ReadByte(ppValue),
			CorElementType.U2 => (ushort)Marshal.ReadInt16(ppValue),
			CorElementType.U4 => (uint)Marshal.ReadInt32(ppValue),
			CorElementType.U8 => (ulong)Marshal.ReadInt64(ppValue),
			CorElementType.R4 => BitConverter.Int32BitsToSingle(Marshal.ReadInt32(ppValue)),
			CorElementType.R8 => BitConverter.Int64BitsToDouble(Marshal.ReadInt64(ppValue)),
			CorElementType.STRING => Marshal.PtrToStringUni(ppValue, pcchValue),
			CorElementType.CLASS => null,
			CorElementType.OBJECT => null,
			CorElementType.VOID => null,
			_ => throw new ArgumentOutOfRangeException(nameof(elementType), $"Unsupported literal type: {elementType}"),
		};
		return result;
	}

	private static string? GetFriendlyTypeName(CorElementType elementType)
	{
		return elementType switch
		{
			CorElementType.VOID => "void",
			CorElementType.BOOLEAN => "bool",
			CorElementType.CHAR => "char",
			CorElementType.I1 => "sbyte",
			CorElementType.U1 => "byte",
			CorElementType.I2 => "short",
			CorElementType.U2 => "ushort",
			CorElementType.I4 => "int",
			CorElementType.U4 => "uint",
			CorElementType.I8 => "long",
			CorElementType.U8 => "ulong",
			CorElementType.R4 => "float",
			CorElementType.R8 => "double",
			CorElementType.STRING => "string",
			CorElementType.OBJECT => "object", // Should we ever see this?
			CorElementType.I => "nint",
			CorElementType.U => "nuint",
			_ => null
		};
	}
}
