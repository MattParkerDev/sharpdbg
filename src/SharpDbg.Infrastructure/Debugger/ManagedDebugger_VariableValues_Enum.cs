using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using ICorDebugSharp;
using EntityHandle = System.Reflection.Metadata.EntityHandle;

namespace SharpDbg.Infrastructure.Debugger;

public partial class ManagedDebugger
{
	private static string GetEnumDisplayValue(IMetaDataImport metaDataImport, mdTypeDef enumTypeDef, string valueAsString)
	{
		var hasFlagsAttribute = metaDataImport.TryGetCustomAttributeByName(enumTypeDef, "System.FlagsAttribute", out _, out _) is Cor.S_OK;

		// Fast path: exact match
		var exact = GetEnumNameForValue(metaDataImport, enumTypeDef, valueAsString);
		if (exact is not null) return exact;

		if (hasFlagsAttribute is false) return valueAsString;

		return GetFlagsEnumValue(metaDataImport, enumTypeDef, valueAsString);
	}

	private static string? GetEnumNameForValue(IMetaDataImport metaDataImport, mdTypeDef enumTypeDef, string valueAsString)
	{
		var fields = metaDataImport.EnumFields(enumTypeDef);
		foreach (var field in fields)
		{
			const CorFieldAttr requiredAttributesForEnumOption = CorFieldAttr.fdPublic | CorFieldAttr.fdStatic | CorFieldAttr.fdLiteral | CorFieldAttr.fdHasDefault;
			var fieldProps = metaDataImport.GetFieldProps(field);
			if ((fieldProps.pdwAttr & requiredAttributesForEnumOption) != requiredAttributesForEnumOption) continue;
			var fieldValue = GetLiteralValue(fieldProps.ppValue, fieldProps.pdwCPlusTypeFlag, fieldProps.pcchValue);
			if (fieldValue?.ToString() == valueAsString)
			{
				return fieldProps.szField;
			}
		}
		return null;
	}

	private static string GetFlagsEnumValue(IMetaDataImport metaDataImport, mdTypeDef enumTypeDef, string valueAsString)
	{
		if (!ulong.TryParse(valueAsString, out var enumValue))
			return valueAsString;

		ulong remaining = enumValue;

		// value -> name, ordered by value (ascending)
		var flags = new SortedDictionary<ulong, string>();

		foreach (var field in metaDataImport.EnumFields(enumTypeDef))
		{
			const CorFieldAttr requiredAttributesForEnumOption = CorFieldAttr.fdPublic | CorFieldAttr.fdStatic | CorFieldAttr.fdLiteral | CorFieldAttr.fdHasDefault;

			var fieldProps = metaDataImport.GetFieldProps(field);
			if ((fieldProps.pdwAttr & requiredAttributesForEnumOption) != requiredAttributesForEnumOption) continue;

			var fieldValueObj = GetLiteralValue(fieldProps.ppValue, fieldProps.pdwCPlusTypeFlag, fieldProps.pcchValue);

			ulong fieldValue = Convert.ToUInt64(fieldValueObj);

			// Zero flag is excluded from OR expressions
			if (fieldValue is 0) continue;

			// Exact match already handled earlier
			if ((fieldValue & remaining) == fieldValue)
			{
				flags[fieldValue] = fieldProps.szField;
				remaining &= ~fieldValue;
			}
		}

		// Only return flags if we fully decomposed the value
		if (flags.Count > 0 && remaining == 0)
		{
			return string.Join(" | ", flags.Values);
		}

		// Fallback: numeric value
		return enumValue.ToString();
	}

	// All of the below is for const fields
	private static bool TryGetEnumDisplayValue((string FriendlyName, ModuleInfo Module, EntityHandle Handle) fieldType, string value, out string displayValue)
	{
		displayValue = value;
		if (fieldType.Handle.IsNil) return false;

		var metadataImport = fieldType.Module.Module.GetMetaDataInterface<IMetaDataImport>();
		IMetaDataImport? enumMetadataImport;
		mdTypeDef enumTypeDef;
		nint resolvedScope = 0;
		if (fieldType.Handle.Kind is HandleKind.TypeDefinition)
		{
			enumMetadataImport = metadataImport;
			enumTypeDef = (mdTypeDef)MetadataTokens.GetToken(fieldType.Handle);
		}
		else if (fieldType.Handle.Kind is HandleKind.TypeReference)
		{
			var iid = Cor.IID_IMetaDataImport;
			var hr = metadataImport.TryResolveTypeRef((mdTypeRef)MetadataTokens.GetToken(fieldType.Handle), ref iid, out resolvedScope, out enumTypeDef);
			if (hr < 0 || resolvedScope is 0) return false;
			unsafe
			{
				// TODO: update ICorDebugSharp to return resolvedScope as a COM interface
				enumMetadataImport = ComInterfaceMarshaller<IMetaDataImport>.ConvertToManaged((void*)resolvedScope);
			}
			if (enumMetadataImport is null) return false;
		}
		else
		{
			return false;
		}

		try
		{
			if (IsEnumType(enumMetadataImport, enumTypeDef) is false) return false;
			displayValue = GetEnumDisplayValue(enumMetadataImport, enumTypeDef, value);
			return true;
		}
		finally
		{
			unsafe
			{
				if (resolvedScope != 0) ComInterfaceMarshaller<IMetaDataImport>.Free((void*)resolvedScope);
			}
		}
	}

	private static bool IsEnumType(IMetaDataImport metadataImport, mdTypeDef typeDef)
	{
		var baseType = metadataImport.GetTypeDefProps(typeDef).ptkExtends;
		var baseTypeName = MetadataTokens.Handle(baseType).Kind switch
		{
			HandleKind.TypeDefinition => metadataImport.GetTypeDefProps((mdTypeDef)(int)baseType).szTypeDef,
			HandleKind.TypeReference => metadataImport.GetTypeRefProps((mdTypeRef)(int)baseType).szName,
			_ => null
		};
		return baseTypeName is "System.Enum";
	}

	private sealed class FieldTypeHandleProvider : ISignatureTypeProvider<EntityHandle, object?>
	{
		public static FieldTypeHandleProvider Instance { get; } = new();
		public EntityHandle GetArrayType(EntityHandle elementType, ArrayShape shape) => default;
		public EntityHandle GetByReferenceType(EntityHandle elementType) => elementType;
		public EntityHandle GetFunctionPointerType(MethodSignature<EntityHandle> signature) => default;
		public EntityHandle GetGenericInstantiation(EntityHandle genericType, ImmutableArray<EntityHandle> typeArguments) => genericType;
		public EntityHandle GetGenericMethodParameter(object? genericContext, int index) => default;
		public EntityHandle GetGenericTypeParameter(object? genericContext, int index) => default;
		public EntityHandle GetModifiedType(EntityHandle modifier, EntityHandle unmodifiedType, bool isRequired) => unmodifiedType;
		public EntityHandle GetPinnedType(EntityHandle elementType) => elementType;
		public EntityHandle GetPointerType(EntityHandle elementType) => default;
		public EntityHandle GetPrimitiveType(PrimitiveTypeCode typeCode) => default;
		public EntityHandle GetSZArrayType(EntityHandle elementType) => default;
		public EntityHandle GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => handle;
		public EntityHandle GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => handle;
		public EntityHandle GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
			reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
	}
}
