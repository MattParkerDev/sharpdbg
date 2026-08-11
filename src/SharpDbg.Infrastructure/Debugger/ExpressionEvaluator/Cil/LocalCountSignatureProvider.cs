using System.Collections.Immutable;
using System.Reflection.Metadata;

namespace SharpDbg.Infrastructure.Debugger.ExpressionEvaluator.Cil;

// 🤖
internal sealed class LocalCountSignatureProvider : ISignatureTypeProvider<object, object?>
{
	public static LocalCountSignatureProvider Instance { get; } = new();
	private static readonly object _type = new();
	public object GetArrayType(object elementType, ArrayShape shape) => _type;
	public object GetByReferenceType(object elementType) => _type;
	public object GetFunctionPointerType(MethodSignature<object> signature) => _type;
	public object GetGenericInstantiation(object genericType, ImmutableArray<object> typeArguments) => _type;
	public object GetGenericMethodParameter(object? genericContext, int index) => _type;
	public object GetGenericTypeParameter(object? genericContext, int index) => _type;
	public object GetModifiedType(object modifier, object unmodifiedType, bool isRequired) => _type;
	public object GetPinnedType(object elementType) => _type;
	public object GetPointerType(object elementType) => _type;
	public object GetPrimitiveType(PrimitiveTypeCode typeCode) => _type;
	public object GetSZArrayType(object elementType) => _type;
	public object GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => _type;
	public object GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => _type;
	public object GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) => _type;
}
