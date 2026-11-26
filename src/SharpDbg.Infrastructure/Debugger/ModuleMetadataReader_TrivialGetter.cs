using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace SharpDbg.Infrastructure.Debugger;

/// A getter whose body only returns one of the instance fields of its own type, e.g. an auto-property's getter or List&lt;T&gt;.Count.
/// Reading the field gives the same value as calling the getter, without a func eval.
/// <param name="FieldToken">The mdFieldDef of the field returned, on the getter's declaring type</param>
/// <param name="IsOverridable">Whether a derived type can override the getter, so it only applies when the receiver's type is the getter's declaring type</param>
internal readonly record struct TrivialGetter(int FieldToken, bool IsOverridable);

public partial class ModuleMetadataReader
{
	private readonly Dictionary<int, TrivialGetter?> _trivialGetters = new();

	/// Returns the field a parameterless instance method returns, if its body is only 'ldarg.0; ldfld; ret' (ignoring nops),
	/// or the equivalent a debug build emits for a getter with a block body: 'ldarg.0; ldfld; stloc.0; br.s 0; ldloc.0; ret'
	internal TrivialGetter? GetTrivialGetter(int methodToken)
	{
		if (_trivialGetters.TryGetValue(methodToken, out var trivialGetter)) return trivialGetter;
		try
		{
			trivialGetter = FindTrivialGetter(MetadataTokens.MethodDefinitionHandle(methodToken));
		}
		catch (BadImageFormatException)
		{
			trivialGetter = null;
		}
		_trivialGetters[methodToken] = trivialGetter;
		return trivialGetter;
	}

	private TrivialGetter? FindTrivialGetter(MethodDefinitionHandle methodHandle)
	{
		var reader = _peMetadataReader;
		var method = reader.GetMethodDefinition(methodHandle);
		var attributes = method.Attributes;
		if ((attributes & MethodAttributes.Static) != 0 || (attributes & MethodAttributes.Abstract) != 0 || method.RelativeVirtualAddress is 0) return null;
		if ((method.ImplAttributes & MethodImplAttributes.CodeTypeMask) is not MethodImplAttributes.IL) return null;
		if (method.GetGenericParameters().Count is not 0) return null;
		var signature = reader.GetBlobReader(method.Signature);
		var header = signature.ReadSignatureHeader();
		if (header.Kind is not SignatureKind.Method || signature.ReadCompressedInteger() is not 0) return null;

		var il = _peReader.GetMethodBody(method.RelativeVirtualAddress).GetILContent().AsSpan();
		var fieldToken = MatchTrivialGetterBody(il);
		if (fieldToken is null) return null;

		var declaringTypeHandle = method.GetDeclaringType();
		var fieldHandle = ResolveOwnField(MetadataTokens.EntityHandle(fieldToken.Value), declaringTypeHandle);
		if (fieldHandle is not { } resolvedField) return null;

		var declaringType = reader.GetTypeDefinition(declaringTypeHandle);
		var isOverridable = (attributes & MethodAttributes.Virtual) != 0 && (attributes & MethodAttributes.Final) == 0 && (declaringType.Attributes & TypeAttributes.Sealed) == 0;
		return new TrivialGetter(MetadataTokens.GetToken(resolvedField), isOverridable);
	}

	private static int? MatchTrivialGetterBody(ReadOnlySpan<byte> il)
	{
		const byte nop = 0x00, ldarg0 = 0x02, ldloc0 = 0x06, stloc0 = 0x0A, brS = 0x2B, ret = 0x2A, ldfld = 0x7B;
		// The opcodes of the body with nops dropped. Only the few opcodes the patterns use are decoded, and any other ends the match
		Span<byte> opcodes = stackalloc byte[6];
		var count = 0;
		int? fieldToken = null;
		for (var i = 0; i < il.Length; i++)
		{
			var opcode = il[i];
			if (opcode is nop) continue;
			if (count == opcodes.Length) return null;
			switch (opcode)
			{
				case ldarg0 or ldloc0 or stloc0 or ret:
					break;
				case ldfld:
					if (i + 4 >= il.Length || fieldToken is not null) return null;
					fieldToken = BinaryPrimitives.ReadInt32LittleEndian(il.Slice(i + 1, 4));
					i += 4;
					break;
				case brS:
					// Only a branch to the next instruction, which a debug build emits before a block body's return
					if (i + 1 >= il.Length || il[i + 1] is not 0) return null;
					i += 1;
					break;
				default:
					return null;
			}
			opcodes[count++] = opcode;
		}
		return opcodes[..count] is [ldarg0, ldfld, ret] or [ldarg0, ldfld, stloc0, brS, ldloc0, ret] ? fieldToken : null;
	}

	/// The non-static field of <paramref name="declaringTypeHandle"/> that <paramref name="fieldHandle"/> refers to, whether directly
	/// or, in a generic type, through a MemberRef on the type's own instantiation
	private FieldDefinitionHandle? ResolveOwnField(EntityHandle fieldHandle, TypeDefinitionHandle declaringTypeHandle)
	{
		var reader = _peMetadataReader;
		FieldDefinitionHandle resolved;
		if (fieldHandle.Kind is HandleKind.FieldDefinition)
		{
			resolved = (FieldDefinitionHandle)fieldHandle;
			if (reader.GetFieldDefinition(resolved).GetDeclaringType() != declaringTypeHandle) return null;
		}
		else if (fieldHandle.Kind is HandleKind.MemberReference)
		{
			var memberReference = reader.GetMemberReference((MemberReferenceHandle)fieldHandle);
			if (memberReference.GetKind() is not MemberReferenceKind.Field || memberReference.Parent.Kind is not HandleKind.TypeSpecification) return null;
			var typeSpecification = reader.GetTypeSpecification((TypeSpecificationHandle)memberReference.Parent);
			var typeSignature = reader.GetBlobReader(typeSpecification.Signature);
			if (typeSignature.ReadSignatureTypeCode() is not SignatureTypeCode.GenericTypeInstance) return null;
			if (typeSignature.ReadSignatureTypeCode() is not SignatureTypeCode.TypeHandle) return null;
			if (typeSignature.ReadTypeHandle() is not { Kind: HandleKind.TypeDefinition } genericType || (TypeDefinitionHandle)genericType != declaringTypeHandle) return null;
			// Matched by name alone, as C# never overloads field names by type
			var fieldName = reader.GetString(memberReference.Name);
			resolved = default;
			foreach (var field in reader.GetTypeDefinition(declaringTypeHandle).GetFields())
			{
				if (reader.StringComparer.Equals(reader.GetFieldDefinition(field).Name, fieldName) is false) continue;
				resolved = field;
				break;
			}
			if (resolved.IsNil) return null;
		}
		else
		{
			return null;
		}
		return (reader.GetFieldDefinition(resolved).Attributes & FieldAttributes.Static) == 0 ? resolved : null;
	}
}
