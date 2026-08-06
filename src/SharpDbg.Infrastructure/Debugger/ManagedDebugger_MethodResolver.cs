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
		bool idsEmpty,
		bool[]? argsByRef = null)
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
			if (IsMethodParameterMatch(method, args, argsByRef)) return method;
		}

		var baseType = type.Base;
		if (baseType is not null)
		{
			var baseMethod = FindMethodOnType(baseType, methodName, args, searchStatic, idsEmpty, argsByRef);
			if (baseMethod is not null) return baseMethod;
		}

		// Extension methods are declared on static classes, not the receiver type, so only look them up once instance member search has failed.
		if (searchStatic is false)
		{
			return FindExtensionMethod(type, methodName, args, argsByRef);
		}

		return null;
	}

	private ICorDebugFunction? FindExtensionMethod(ICorDebugType receiverType, string methodName, ICorDebugValue[] args, bool[]? argsByRef = null)
	{
		foreach (var moduleInfo in _modules.Values)
		{
			var module = moduleInfo.Module;
			var metadataImport = module.GetMetaDataInterface<IMetaDataImport>();

			foreach (var typeToken in metadataImport.EnumTypeDefs())
			{
				// Extension methods are declared in static classes marked with [Extension], so skip everything else up front
				if (metadataImport.HasAnyAttribute(typeToken, [AttributeConstants.ExtensionMethodAttributeName]) is false) continue;

				foreach (var methodToken in metadataImport.EnumMethodsWithName(typeToken, methodName))
				{
					var methodProps = metadataImport.GetMethodProps(methodToken);
					if (methodProps.pdwAttr.IsMdStatic() is false) continue;
					if (metadataImport.HasAnyAttribute(methodToken, [AttributeConstants.ExtensionMethodAttributeName]) is false) continue;

					var method = module.GetFunctionFromToken(methodToken);
					if (IsMethodParameterMatch(method, args, argsByRef, receiverType.Type)) return method;
				}
			}
		}

		return null;
	}


	private bool IsMethodParameterMatch(ICorDebugFunction method, ICorDebugValue[] args, bool[]? argsByRef = null, CorElementType? extensionReceiverType = null)
	{
		var handle = MetadataTokens.Handle(method.Token);
		if (handle.Kind is not HandleKind.MethodDefinition) return false;
		var metadataReader = _modules[method.Class.Module.BaseAddress].MetadataReader.PeMetadataReader;
		var methodDefinition = metadataReader.GetMethodDefinition((MethodDefinitionHandle)handle);
		var parameterTypes = methodDefinition.DecodeSignature(CorElementTypeSignatureProvider.Instance, null).ParameterTypes;

		var expectedParameterCount = args.Length + (extensionReceiverType is not null ? 1 : 0);
		if (parameterTypes.Length != expectedParameterCount) return false;

		if (extensionReceiverType is not null && !parameterTypes[0].Equals(new ParameterType(extensionReceiverType.Value, isByRef: false))) return false;

		for (var i = 0; i < args.Length; i++)
		{
			var parameterType = parameterTypes[i + (extensionReceiverType is not null ? 1 : 0)];
			var argType = args[i].ExactType?.Type ?? args[i].Type;
			if (parameterType.ElementType != argType) return false;

			var isByRefArg = argsByRef?[i] ?? false;
			if (parameterType.IsByRef != isByRefArg) return false;
		}

		return true;
	}

	private readonly struct ParameterType : IEquatable<ParameterType>
	{
		public readonly CorElementType ElementType;
		public readonly bool IsByRef;

		public ParameterType(CorElementType elementType, bool isByRef)
		{
			ElementType = elementType;
			IsByRef = isByRef;
		}

		public bool Equals(ParameterType other) => ElementType == other.ElementType && IsByRef == other.IsByRef;

		public override bool Equals(object? obj) => obj is ParameterType other && Equals(other);

		public override int GetHashCode() => HashCode.Combine(ElementType, IsByRef);
	}

	private sealed class CorElementTypeSignatureProvider : ISignatureTypeProvider<ParameterType, object?>
	{
		public static CorElementTypeSignatureProvider Instance { get; } = new();

		private static ParameterType ByValue(CorElementType elementType) => new(elementType, isByRef: false);

		public ParameterType GetArrayType(ParameterType elementType, ArrayShape shape) => ByValue(CorElementType.ARRAY);
		public ParameterType GetByReferenceType(ParameterType elementType) => new(elementType.ElementType, isByRef: true);
		public ParameterType GetFunctionPointerType(MethodSignature<ParameterType> signature) => ByValue(CorElementType.FNPTR);
		public ParameterType GetGenericInstantiation(ParameterType genericType, ImmutableArray<ParameterType> typeArguments) => ByValue(CorElementType.GENERICINST);
		public ParameterType GetGenericMethodParameter(object? genericContext, int index) => ByValue(CorElementType.MVAR);
		public ParameterType GetGenericTypeParameter(object? genericContext, int index) => ByValue(CorElementType.VAR);
		public ParameterType GetModifiedType(ParameterType modifier, ParameterType unmodifiedType, bool isRequired) => unmodifiedType;
		public ParameterType GetPinnedType(ParameterType elementType) => elementType;
		public ParameterType GetPointerType(ParameterType elementType) => ByValue(CorElementType.PTR);
		public ParameterType GetSZArrayType(ParameterType elementType) => ByValue(CorElementType.SZARRAY);

		public ParameterType GetPrimitiveType(PrimitiveTypeCode typeCode) => ByValue(typeCode switch
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
		});

		public ParameterType GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
			ByValue(rawTypeKind == (byte)SignatureTypeKind.ValueType ? CorElementType.VALUETYPE : CorElementType.CLASS);

		public ParameterType GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
			ByValue(rawTypeKind == (byte)SignatureTypeKind.ValueType ? CorElementType.VALUETYPE : CorElementType.CLASS);

		public ParameterType GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
			reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
	}
}
