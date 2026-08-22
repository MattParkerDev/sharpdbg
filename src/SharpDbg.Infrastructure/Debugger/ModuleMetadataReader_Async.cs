using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace SharpDbg.Infrastructure.Debugger;

public partial class ModuleMetadataReader
{
	public int? GetStateMachineKickoffMethodToken(int moveNextMethodToken)
	{
		var moveNextHandle = MetadataTokens.MethodDefinitionHandle(moveNextMethodToken);
		if (_pdbMetadataReader is not null)
		{
			var kickoffHandle = _pdbMetadataReader.GetMethodDebugInformation(moveNextHandle).GetStateMachineKickoffMethod();
			if (kickoffHandle.IsNil is false) return MetadataTokens.GetToken(kickoffHandle);
		}

		// Metadata-only fallback. Match the generated type referenced by the
		// kickoff method's state-machine attribute so overloads remain unambiguous.
		var moveNext = _peMetadataReader.GetMethodDefinition(moveNextHandle);
		var stateMachineHandle = moveNext.GetDeclaringType();
		var stateMachineType = _peMetadataReader.GetTypeDefinition(stateMachineHandle);
		var declaringTypeHandle = stateMachineType.GetDeclaringType();
		if (declaringTypeHandle.IsNil) return null;
		foreach (var candidateHandle in _peMetadataReader.GetTypeDefinition(declaringTypeHandle).GetMethods())
		{
			var candidate = _peMetadataReader.GetMethodDefinition(candidateHandle);
			if (candidate.GetCustomAttributes().Any(attribute => StateMachineAttributeMatches(attribute, stateMachineHandle)))
				return MetadataTokens.GetToken(candidateHandle);
		}

		return null;
	}

	private bool StateMachineAttributeMatches(CustomAttributeHandle attributeHandle,
		TypeDefinitionHandle stateMachineHandle)
	{
		var attribute = _peMetadataReader.GetCustomAttribute(attributeHandle);
		var attributeType = attribute.Constructor.Kind switch
		{
			HandleKind.MethodDefinition => _peMetadataReader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType(),
			HandleKind.MemberReference => _peMetadataReader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent,
			_ => default
		};
		if (attributeType.Kind is not (HandleKind.TypeDefinition or HandleKind.TypeReference)) return false;
		var (attributeNamespace, attributeName) = attributeType.Kind is HandleKind.TypeDefinition
			? GetTypeName((TypeDefinitionHandle)attributeType)
			: GetTypeName((TypeReferenceHandle)attributeType);
		if (attributeNamespace is not "System.Runtime.CompilerServices" || attributeName is not
			    ("AsyncStateMachineAttribute" or "IteratorStateMachineAttribute" or "AsyncIteratorStateMachineAttribute")) return false;

		var value = _peMetadataReader.GetBlobReader(attribute.Value);
		if (value.ReadUInt16() != 1) return false;
		var serializedTypeName = value.ReadSerializedString();
		var stateMachineName = GetSerializedTypeName(stateMachineHandle);
		return serializedTypeName == stateMachineName || serializedTypeName?.StartsWith(stateMachineName + ",", StringComparison.Ordinal) is true;
	}

	private (string Namespace, string Name) GetTypeName(TypeDefinitionHandle handle)
	{
		var type = _peMetadataReader.GetTypeDefinition(handle);
		return (_peMetadataReader.GetString(type.Namespace), _peMetadataReader.GetString(type.Name));
	}

	private (string Namespace, string Name) GetTypeName(TypeReferenceHandle handle)
	{
		var type = _peMetadataReader.GetTypeReference(handle);
		return (_peMetadataReader.GetString(type.Namespace), _peMetadataReader.GetString(type.Name));
	}

	private string GetSerializedTypeName(TypeDefinitionHandle handle)
	{
		var type = _peMetadataReader.GetTypeDefinition(handle);
		var name = _peMetadataReader.GetString(type.Name);
		var declaringType = type.GetDeclaringType();
		if (declaringType.IsNil is false) return $"{GetSerializedTypeName(declaringType)}+{name}";
		var @namespace = _peMetadataReader.GetString(type.Namespace);
		return string.IsNullOrEmpty(@namespace) ? name : $"{@namespace}.{name}";
	}
}
