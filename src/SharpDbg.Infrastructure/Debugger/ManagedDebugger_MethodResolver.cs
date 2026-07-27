using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using ICorDebugSharp;

namespace SharpDbg.Infrastructure.Debugger;

public partial class ManagedDebugger
{
	internal ICorDebugFunction? FindMethodOnType(
		ICorDebugType type,
		string methodName,
		ICorDebugValue[] args,
		bool searchStatic,
		bool idsEmpty)
	{
		var typeClass = type.Class;
		var module = typeClass.Module;
		var metadataImport = module.GetMetaDataInterface<IMetaDataImport>();

		foreach (var methodToken in metadataImport.EnumMethods(typeClass.Token))
		{
			var methodProps = metadataImport.GetMethodProps(methodToken);
			if (methodProps.szMethod != methodName) continue;

			var isStatic = methodProps.pdwAttr.IsMdStatic();
			if ((searchStatic && !isStatic) || (!searchStatic && isStatic && !idsEmpty)) continue;

			var method = module.GetFunctionFromToken(methodToken);
			if (IsMethodParameterMatch(method, args)) return method;
		}

		var baseType = type.Base;
		return baseType is null ? null : FindMethodOnType(baseType, methodName, args, searchStatic, idsEmpty);
	}

	private bool IsMethodParameterMatch(ICorDebugFunction method, ICorDebugValue[] args)
	{
		var handle = MetadataTokens.Handle(method.Token);
		if (handle.Kind is not HandleKind.MethodDefinition) return false;
		var metadataReader = _modules[method.Class.Module.BaseAddress].MetadataReader.PeMetadataReader;
		var methodDefinition = metadataReader.GetMethodDefinition((MethodDefinitionHandle)handle);
		var parameterTypes = methodDefinition.DecodeSignature(CorElementTypeSignatureProvider.Instance, null).ParameterTypes;
		if (parameterTypes.Length != args.Length) return false;

		for (var i = 0; i < args.Length; i++)
		{
			var argType = args[i].ExactType?.Type ?? args[i].Type;
			if (parameterTypes[i] != argType) return false;
		}

		return true;
	}

	private sealed class CorElementTypeSignatureProvider : ISignatureTypeProvider<CorElementType, object?>
	{
		public static CorElementTypeSignatureProvider Instance { get; } = new();

		public CorElementType GetArrayType(CorElementType elementType, ArrayShape shape) => CorElementType.ARRAY;
		public CorElementType GetByReferenceType(CorElementType elementType) => CorElementType.BYREF;
		public CorElementType GetFunctionPointerType(MethodSignature<CorElementType> signature) => CorElementType.FNPTR;
		public CorElementType GetGenericInstantiation(CorElementType genericType, ImmutableArray<CorElementType> typeArguments) => CorElementType.GENERICINST;
		public CorElementType GetGenericMethodParameter(object? genericContext, int index) => CorElementType.MVAR;
		public CorElementType GetGenericTypeParameter(object? genericContext, int index) => CorElementType.VAR;
		public CorElementType GetModifiedType(CorElementType modifier, CorElementType unmodifiedType, bool isRequired) => unmodifiedType;
		public CorElementType GetPinnedType(CorElementType elementType) => elementType;
		public CorElementType GetPointerType(CorElementType elementType) => CorElementType.PTR;
		public CorElementType GetSZArrayType(CorElementType elementType) => CorElementType.SZARRAY;

		public CorElementType GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
		{
			PrimitiveTypeCode.Void => CorElementType.VOID,
			PrimitiveTypeCode.Boolean => CorElementType.BOOLEAN,
			PrimitiveTypeCode.Char => CorElementType.CHAR,
			PrimitiveTypeCode.SByte => CorElementType.I1,
			PrimitiveTypeCode.Byte => CorElementType.U1,
			PrimitiveTypeCode.Int16 => CorElementType.I2,
			PrimitiveTypeCode.UInt16 => CorElementType.U2,
			PrimitiveTypeCode.Int32 => CorElementType.I4,
			PrimitiveTypeCode.UInt32 => CorElementType.U4,
			PrimitiveTypeCode.Int64 => CorElementType.I8,
			PrimitiveTypeCode.UInt64 => CorElementType.U8,
			PrimitiveTypeCode.Single => CorElementType.R4,
			PrimitiveTypeCode.Double => CorElementType.R8,
			PrimitiveTypeCode.String => CorElementType.STRING,
			PrimitiveTypeCode.TypedReference => CorElementType.TYPEDBYREF,
			PrimitiveTypeCode.IntPtr => CorElementType.I,
			PrimitiveTypeCode.UIntPtr => CorElementType.U,
			PrimitiveTypeCode.Object => CorElementType.OBJECT,
			_ => CorElementType.END
		};

		public CorElementType GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
			rawTypeKind == (byte)SignatureTypeKind.ValueType ? CorElementType.VALUETYPE : CorElementType.CLASS;

		public CorElementType GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
			rawTypeKind == (byte)SignatureTypeKind.ValueType ? CorElementType.VALUETYPE : CorElementType.CLASS;

		public CorElementType GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
			reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
	}
}
