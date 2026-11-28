using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text.RegularExpressions;
using ICorDebugSharp;
using Microsoft.CodeAnalysis.CSharp;
using SharpDbg.Infrastructure.Debugger.ExpressionEvaluator;
using SharpDbg.Infrastructure.Debugger.Models.Response;

namespace SharpDbg.Infrastructure.Debugger;

public partial class ManagedDebugger
{
	// The container a member is assigned through, bound as an alias so the expression reaches the listed value itself,
	// e.g. a struct local's own storage, rather than a re-evaluated path to it
	private const string ContainerAlias = "$container";

	public async Task<VariableInfo> SetVariable(int variablesReferenceInt, string name, string value)
	{
		_logger?.Invoke($"SetVariable: {name} = {value}");
		if (_variableManager.GetReference(variablesReferenceInt) is not { } variablesReference) throw new ArgumentException("Invalid variables reference");
		var (target, container) = GetAssignmentTarget(variablesReference, name);
		var boundVariable = container is null ? null : new EvaluationAlias(ContainerAlias, GetAssemblyQualifiedTypeName(container.UnwrapDebugValue().ExactType), container);
		return await EvaluateInFrame($"{target} = {value}", variablesReference.ThreadId, variablesReference.FrameStackDepth, boundVariable, throwOnError: true);
	}

	private (string Target, ICorDebugValue? Container) GetAssignmentTarget(VariablesReference variablesReference, string name)
	{
		switch (variablesReference.ReferenceKind)
		{
			case StoredReferenceKind.Scope:
				return (RequireIdentifier(name), null);
			case StoredReferenceKind.StackVariable when variablesReference.DebuggerProxyTypeName is not null:
				throw new InvalidOperationException("The members of a debugger type proxy cannot be set. Set them in the 'Raw View' instead");
			case StoredReferenceKind.StackVariable or StoredReferenceKind.RawView or StoredReferenceKind.ArrayRange:
				var container = variablesReference.ObjectValue!;
				return container.UnwrapDebugValue() is ICorDebugArrayValue
					? ($"{ContainerAlias}{RequireArrayIndex(name)}", container)
					: ($"{ContainerAlias}.{RequireIdentifier(name)}", container);
			case StoredReferenceKind.StaticClassVariable:
				var type = variablesReference.ObjectValue!.UnwrapDebugValueToObject().ExactType;
				return ($"{GetCorDebugTypeFriendlyName(type)}.{RequireIdentifier(name)}", null);
			default:
				throw new InvalidOperationException($"Variables in '{variablesReference.ReferenceKind}' cannot be set");
		}

		static string RequireIdentifier(string name) => SyntaxFacts.IsValidIdentifier(name) ? name : throw new InvalidOperationException($"'{name}' cannot be set");
		static string RequireArrayIndex(string name) => ArrayIndexName().IsMatch(name) ? name : throw new InvalidOperationException($"'{name}' cannot be set");
	}

	[GeneratedRegex(@"^\[\d+(, \d+)*\]$")]
	private static partial Regex ArrayIndexName();

	private string GetAssemblyQualifiedTypeName(ICorDebugType type) => $"{GetReflectionTypeName(type)}, {GetAssemblyName(type)}";

	private string GetReflectionTypeName(ICorDebugType type)
	{
		switch (type.Type)
		{
			case CorElementType.SZARRAY:
				return $"{GetReflectionTypeName(type.FirstTypeParameter)}[]";
			case CorElementType.ARRAY:
				return $"{GetReflectionTypeName(type.FirstTypeParameter)}[{new string(',', type.Rank - 1)}]";
			case CorElementType.CLASS or CorElementType.VALUETYPE:
				var reader = _modules[type.Class.Module.BaseAddress].MetadataReader.PeMetadataReader;
				var name = GetTypeDefinitionName(reader, MetadataTokens.TypeDefinitionHandle(type.Class.Token.Rid));
				var typeArguments = type.TypeParameters;
				return typeArguments.Length is 0 ? name : $"{name}[[{string.Join("],[", typeArguments.Select(GetAssemblyQualifiedTypeName))}]]";
			default:
				return GetPrimitiveReflectionTypeName(type.Type) ?? throw new NotSupportedException($"A '{type.Type}' value cannot be set through");
		}

		static string GetTypeDefinitionName(MetadataReader reader, TypeDefinitionHandle handle)
		{
			var definition = reader.GetTypeDefinition(handle);
			var name = reader.GetString(definition.Name);
			var declaringType = definition.GetDeclaringType();
			if (declaringType.IsNil is false) return $"{GetTypeDefinitionName(reader, declaringType)}+{name}";
			var @namespace = reader.GetString(definition.Namespace);
			return string.IsNullOrEmpty(@namespace) ? name : $"{@namespace}.{name}";
		}
	}

	private string GetAssemblyName(ICorDebugType type)
	{
		if (type.Type is CorElementType.SZARRAY or CorElementType.ARRAY) return GetAssemblyName(type.FirstTypeParameter);
		var module = type.Type is CorElementType.CLASS or CorElementType.VALUETYPE
			? _modules[type.Class.Module.BaseAddress]
			: AllModules.First(module => module.ModuleName is "System.Private.CoreLib.dll");
		var reader = module.MetadataReader.PeMetadataReader;
		if (reader.IsAssembly is false) return Path.GetFileNameWithoutExtension(module.Module.Name);
		// The full identity, as Roslyn only binds the type to a referenced assembly of the same identity
		var definition = reader.GetAssemblyDefinition();
		var assemblyName = new AssemblyName
		{
			Name = reader.GetString(definition.Name),
			Version = definition.Version,
			CultureName = definition.Culture.IsNil ? null : reader.GetString(definition.Culture)
		};
		if (definition.PublicKey.IsNil is false) assemblyName.SetPublicKey(reader.GetBlobBytes(definition.PublicKey));
		return assemblyName.FullName;
	}

	private static string? GetPrimitiveReflectionTypeName(CorElementType elementType) => elementType switch
	{
		CorElementType.BOOLEAN => "System.Boolean",
		CorElementType.CHAR => "System.Char",
		CorElementType.I1 => "System.SByte",
		CorElementType.U1 => "System.Byte",
		CorElementType.I2 => "System.Int16",
		CorElementType.U2 => "System.UInt16",
		CorElementType.I4 => "System.Int32",
		CorElementType.U4 => "System.UInt32",
		CorElementType.I8 => "System.Int64",
		CorElementType.U8 => "System.UInt64",
		CorElementType.R4 => "System.Single",
		CorElementType.R8 => "System.Double",
		CorElementType.I => "System.IntPtr",
		CorElementType.U => "System.UIntPtr",
		CorElementType.STRING => "System.String",
		CorElementType.OBJECT => "System.Object",
		_ => null
	};
}
