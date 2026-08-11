using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Collections.Immutable;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using ICorDebugSharp;

namespace SharpDbg.Infrastructure.Debugger.ExpressionEvaluator.Cil;

internal readonly record struct ResolvedRuntimeType(ModuleInfo Module, TypeDefinitionHandle Handle, ImmutableArray<ResolvedCilType> TypeArguments = default)
{
	public ICorDebugClass Class => Module.Module.GetClassFromToken((mdTypeDef)MetadataTokens.GetToken(Handle));
}

internal readonly record struct ResolvedRuntimeField(ResolvedRuntimeType DeclaringType, FieldDefinitionHandle Handle, bool IsStatic)
{
	public mdFieldDef Token => (mdFieldDef)MetadataTokens.GetToken(Handle);
}

internal readonly record struct ResolvedRuntimeMethod(ResolvedRuntimeType DeclaringType, MethodDefinitionHandle Handle, string Name, MethodSignature<string> Signature, bool IsStatic, ImmutableArray<ResolvedCilType> MethodTypeArguments = default)
{
	public ICorDebugFunction Function => DeclaringType.Module.Module.GetFunctionFromToken((mdMethodDef)MetadataTokens.GetToken(Handle));
}

internal readonly record struct ResolvedEvaluationMethod(MethodDefinitionHandle Handle, MethodSignature<string> Signature, bool IsStatic);
internal sealed record ResolvedCilType(PrimitiveTypeCode? Primitive, ResolvedRuntimeType? RuntimeType, ResolvedCilType? ElementType = null, int ArrayRank = 0, bool IsSzArray = false);

// 🤖
internal sealed class EvaluationMetadataResolver(ManagedDebugger debugger, MetadataReader evaluationReader, PEReader evaluationPeReader, ICorDebugAppDomain appDomain, ICorDebugType[] typeGenericArguments, ICorDebugType[] methodGenericArguments, ModuleInfo? currentFrameModule)
{
	/// <summary>
	/// The concrete generic arguments the generated evaluation method's type parameters (<c>!i</c> type
	/// parameters followed by <c>!!i</c> method type parameters) map to. For a frame evaluation these are the
	/// current frame's instantiation; for a type-context evaluation (e.g. DebuggerDisplay) they are the root
	/// value's own type arguments.
	/// </summary>
	private readonly (ICorDebugType[] TypeArguments, ICorDebugType[] MethodArguments) _genericArguments = (typeGenericArguments, methodGenericArguments);

	public string ResolveUserString(int token) => evaluationReader.GetUserString(MetadataTokens.UserStringHandle(token));

	public ResolvedCilType ResolveMethodReturnType(MethodDefinitionHandle handle) =>
		evaluationReader.GetMethodDefinition(handle).DecodeSignature(new RuntimeTypeSignatureProvider(this), genericContext: null).ReturnType;

	public ResolvedCilType ResolveTypeToken(int token)
	{
		var handle = MetadataTokens.EntityHandle(token);
		if (handle.Kind == HandleKind.TypeSpecification)
		{
			return evaluationReader.GetTypeSpecification((TypeSpecificationHandle)handle)
				.DecodeSignature(new RuntimeTypeSignatureProvider(this), genericContext: null);
		}
		return new ResolvedCilType(null, ResolveType(handle));
	}

	public ResolvedCilType ResolveGenericTypeParameter(int index) =>
		ResolveGenericParameter(index, _genericArguments.TypeArguments, "!");

	public ResolvedCilType ResolveGenericMethodParameter(int index) =>
		ResolveGenericParameter(index, _genericArguments.MethodArguments, "!!");

	private ResolvedCilType ResolveGenericParameter(int index, IReadOnlyList<ICorDebugType> arguments, string prefix)
	{
		if (index >= 0 && index < arguments.Count) return ResolveCorDebugType(arguments[index]);
		throw new NotSupportedException($"The generic parameter '{prefix}{index}' is not available in the current frame");
	}

	private ResolvedCilType ResolveCorDebugType(ICorDebugType type)
	{
		return type.Type switch
		{
			CorElementType.BOOLEAN => new ResolvedCilType(PrimitiveTypeCode.Boolean, null),
			CorElementType.CHAR => new ResolvedCilType(PrimitiveTypeCode.Char, null),
			CorElementType.I1 => new ResolvedCilType(PrimitiveTypeCode.SByte, null),
			CorElementType.U1 => new ResolvedCilType(PrimitiveTypeCode.Byte, null),
			CorElementType.I2 => new ResolvedCilType(PrimitiveTypeCode.Int16, null),
			CorElementType.U2 => new ResolvedCilType(PrimitiveTypeCode.UInt16, null),
			CorElementType.I4 => new ResolvedCilType(PrimitiveTypeCode.Int32, null),
			CorElementType.U4 => new ResolvedCilType(PrimitiveTypeCode.UInt32, null),
			CorElementType.I8 => new ResolvedCilType(PrimitiveTypeCode.Int64, null),
			CorElementType.U8 => new ResolvedCilType(PrimitiveTypeCode.UInt64, null),
			CorElementType.R4 => new ResolvedCilType(PrimitiveTypeCode.Single, null),
			CorElementType.R8 => new ResolvedCilType(PrimitiveTypeCode.Double, null),
			CorElementType.I => new ResolvedCilType(PrimitiveTypeCode.IntPtr, null),
			CorElementType.U => new ResolvedCilType(PrimitiveTypeCode.UIntPtr, null),
			CorElementType.STRING => new ResolvedCilType(PrimitiveTypeCode.String, null),
			CorElementType.OBJECT => new ResolvedCilType(PrimitiveTypeCode.Object, null),
			CorElementType.CLASS or CorElementType.VALUETYPE => new ResolvedCilType(null, ResolveRuntimeType(type)),
			CorElementType.SZARRAY => new ResolvedCilType(null, null, ResolveCorDebugType(type.FirstTypeParameter), 1, true),
			CorElementType.ARRAY => new ResolvedCilType(null, null, ResolveCorDebugType(type.FirstTypeParameter), type.Rank),
			_ => throw new NotSupportedException($"Cannot resolve a CIL type from CorElementType '{type.Type}'")
		};
	}

	private ResolvedRuntimeType ResolveRuntimeType(ICorDebugType type)
	{
		var @class = type.Class;
		var moduleInfo = debugger.GetModuleInfoForModule(@class.Module);
		var handle = (TypeDefinitionHandle)MetadataTokens.Handle(@class.Token);
		ICorDebugType[] typeParameters;
		try
		{
			typeParameters = type.TypeParameters;
		}
		catch
		{
			typeParameters = [];
		}
		return new ResolvedRuntimeType(moduleInfo, handle, typeParameters.Length == 0 ? default : typeParameters.Select(ResolveCorDebugType).ToImmutableArray());
	}

	public ResolvedRuntimeField ResolveField(int token)
	{
		var handle = MetadataTokens.EntityHandle(token);
		if (handle.Kind != HandleKind.MemberReference)
		{
			throw new NotSupportedException($"Evaluation field token kind '{handle.Kind}' is not supported");
		}

		var member = evaluationReader.GetMemberReference((MemberReferenceHandle)handle);
		var declaringType = ResolveType(member.Parent);
		var name = evaluationReader.GetString(member.Name);
		var reader = declaringType.Module.MetadataReader.PeMetadataReader;
		var type = reader.GetTypeDefinition(declaringType.Handle);
		foreach (var fieldHandle in type.GetFields())
		{
			var field = reader.GetFieldDefinition(fieldHandle);
			if (reader.GetString(field.Name) == name)
			{
				return new ResolvedRuntimeField(declaringType, fieldHandle, (field.Attributes & FieldAttributes.Static) != 0);
			}
		}

		throw new MissingFieldException(GetTypeName(declaringType), name);
	}

	public ResolvedRuntimeMethod ResolveMethod(int token)
	{
		var handle = MetadataTokens.EntityHandle(token);
		ImmutableArray<ResolvedCilType> methodTypeArguments = default;
		if (handle.Kind == HandleKind.MethodSpecification)
		{
			var specification = evaluationReader.GetMethodSpecification((MethodSpecificationHandle)handle);
			methodTypeArguments = specification.DecodeSignature(new RuntimeTypeSignatureProvider(this), genericContext: null);
			handle = specification.Method;
		}
		if (handle.Kind != HandleKind.MemberReference)
		{
			throw new NotSupportedException($"Evaluation method token kind '{handle.Kind}' is not supported");
		}

		var member = evaluationReader.GetMemberReference((MemberReferenceHandle)handle);
		var declaringType = ResolveType(member.Parent);
		var name = evaluationReader.GetString(member.Name);
		var expectedSignature = member.DecodeMethodSignature(SignatureNameProvider.Instance, genericContext: null);
		var reader = declaringType.Module.MetadataReader.PeMetadataReader;
		foreach (var methodHandle in reader.GetTypeDefinition(declaringType.Handle).GetMethods())
		{
			var method = reader.GetMethodDefinition(methodHandle);
			if (reader.GetString(method.Name) != name) continue;
			var signature = method.DecodeSignature(SignatureNameProvider.Instance, genericContext: null);
			if (SignaturesEqual(expectedSignature, signature))
			{
				return new ResolvedRuntimeMethod(declaringType, methodHandle, name, signature, (method.Attributes & MethodAttributes.Static) != 0, methodTypeArguments);
			}
		}

		throw new MissingMethodException(GetTypeName(declaringType), name);
	}

	public bool TryResolveEvaluationMethod(int token, out ResolvedEvaluationMethod result)
	{
		var handle = MetadataTokens.EntityHandle(token);
		if (handle.Kind == HandleKind.MethodSpecification)
		{
			handle = evaluationReader.GetMethodSpecification((MethodSpecificationHandle)handle).Method;
		}
		if (handle.Kind != HandleKind.MethodDefinition)
		{
			result = default;
			return false;
		}
		var methodHandle = (MethodDefinitionHandle)handle;
		var method = evaluationReader.GetMethodDefinition(methodHandle);
		result = new ResolvedEvaluationMethod(
			methodHandle,
			method.DecodeSignature(SignatureNameProvider.Instance, genericContext: null),
			(method.Attributes & MethodAttributes.Static) != 0);
		return true;
	}

	public bool TryResolveDebuggerIntrinsic(int token, out string methodName)
	{
		var handle = MetadataTokens.EntityHandle(token);
		if (handle.Kind == HandleKind.MethodSpecification) handle = evaluationReader.GetMethodSpecification((MethodSpecificationHandle)handle).Method;
		if (handle.Kind == HandleKind.MemberReference)
		{
			var member = evaluationReader.GetMemberReference((MemberReferenceHandle)handle);
			if (member.Parent.Kind == HandleKind.TypeReference)
			{
				var type = evaluationReader.GetTypeReference((TypeReferenceHandle)member.Parent);
				if (evaluationReader.GetString(type.Namespace) == "Microsoft.VisualStudio.Debugger.Clr" &&
					evaluationReader.GetString(type.Name) == "IntrinsicMethods")
				{
					methodName = evaluationReader.GetString(member.Name);
					return true;
				}
			}
		}
		methodName = string.Empty;
		return false;
	}

	public MethodBodyBlock GetEvaluationMethodBody(MethodDefinitionHandle handle) =>
		evaluationPeReader.GetMethodBody(evaluationReader.GetMethodDefinition(handle).RelativeVirtualAddress);

	public int GetEvaluationLocalCount(StandaloneSignatureHandle handle) => handle.IsNil
		? 0
		: evaluationReader.GetStandaloneSignature(handle).DecodeLocalSignature(LocalCountSignatureProvider.Instance, genericContext: null).Length;

	public ICorDebugType GetCorDebugType(ResolvedRuntimeType type)
	{
		var elementType = IsValueType(type) ? CorElementType.VALUETYPE : CorElementType.CLASS;
		var typeArguments = type.TypeArguments.IsDefaultOrEmpty ? [] : type.TypeArguments.Select(GetCorDebugType).ToArray();
		return ((ICorDebugClass2)type.Class).GetParameterizedType(elementType, typeArguments.Length, typeArguments);
	}

	public ICorDebugType GetCorDebugType(ResolvedCilType type)
	{
		if (type.ElementType is not null)
		{
			return ((ICorDebugAppDomain2)appDomain).GetArrayOrPointerType(
				type.IsSzArray ? CorElementType.SZARRAY : CorElementType.ARRAY,
				type.ArrayRank,
				GetCorDebugType(type.ElementType));
		}
		if (type.RuntimeType is { } runtimeType) return GetCorDebugType(runtimeType);
		if (type.Primitive is not { } primitive) throw new NotSupportedException("The CIL type cannot be materialized");

		var (name, elementType) = primitive switch
		{
			PrimitiveTypeCode.Boolean => ("Boolean", CorElementType.VALUETYPE),
			PrimitiveTypeCode.Byte => ("Byte", CorElementType.VALUETYPE),
			PrimitiveTypeCode.SByte => ("SByte", CorElementType.VALUETYPE),
			PrimitiveTypeCode.Char => ("Char", CorElementType.VALUETYPE),
			PrimitiveTypeCode.Int16 => ("Int16", CorElementType.VALUETYPE),
			PrimitiveTypeCode.UInt16 => ("UInt16", CorElementType.VALUETYPE),
			PrimitiveTypeCode.Int32 => ("Int32", CorElementType.VALUETYPE),
			PrimitiveTypeCode.UInt32 => ("UInt32", CorElementType.VALUETYPE),
			PrimitiveTypeCode.Int64 => ("Int64", CorElementType.VALUETYPE),
			PrimitiveTypeCode.UInt64 => ("UInt64", CorElementType.VALUETYPE),
			PrimitiveTypeCode.Single => ("Single", CorElementType.VALUETYPE),
			PrimitiveTypeCode.Double => ("Double", CorElementType.VALUETYPE),
			PrimitiveTypeCode.String => ("String", CorElementType.CLASS),
			PrimitiveTypeCode.Object => ("Object", CorElementType.CLASS),
			PrimitiveTypeCode.IntPtr => ("IntPtr", CorElementType.VALUETYPE),
			PrimitiveTypeCode.UIntPtr => ("UIntPtr", CorElementType.VALUETYPE),
			_ => throw new NotSupportedException($"Primitive CIL type '{primitive}' cannot be materialized")
		};
		var primitiveRuntimeType = FindRuntimeType("System", name);
		return ((ICorDebugClass2)primitiveRuntimeType.Class).GetParameterizedType(elementType, 0, []);
	}

	public string GetRuntimeTypeName(ResolvedRuntimeType type) => GetTypeName(type);
	public int GetRuntimeTypeGenericArity(ResolvedRuntimeType type) =>
		type.Module.MetadataReader.PeMetadataReader.GetTypeDefinition(type.Handle).GetGenericParameters().Count();

	public string GetAssemblyQualifiedTypeName(ResolvedCilType type) => $"{GetReflectionTypeName(type)}, {GetTypeAssemblyName(type)}";

	public ResolvedRuntimeMethod ResolveRuntimeMethod(string @namespace, string typeName, string methodName, params string[] parameterTypes)
	{
		var declaringType = FindRuntimeType(@namespace, typeName);
		var reader = declaringType.Module.MetadataReader.PeMetadataReader;
		foreach (var handle in reader.GetTypeDefinition(declaringType.Handle).GetMethods())
		{
			var method = reader.GetMethodDefinition(handle);
			if (reader.GetString(method.Name) != methodName) continue;
			var signature = method.DecodeSignature(SignatureNameProvider.Instance, genericContext: null);
			if (signature.ParameterTypes.SequenceEqual(parameterTypes))
			{
				return new ResolvedRuntimeMethod(declaringType, handle, methodName, signature, (method.Attributes & MethodAttributes.Static) != 0);
			}
		}
		throw new MissingMethodException($"{@namespace}.{typeName}", methodName);
	}

	private ResolvedRuntimeType ResolveType(EntityHandle handle)
	{
		return handle.Kind switch
		{
			HandleKind.TypeReference => ResolveTypeReference((TypeReferenceHandle)handle),
			HandleKind.TypeSpecification => ResolveTypeSpecification((TypeSpecificationHandle)handle),
			_ => throw new NotSupportedException($"Evaluation type token kind '{handle.Kind}' is not supported")
		};
	}

	private ResolvedRuntimeType ResolveTypeSpecification(TypeSpecificationHandle handle)
	{
		return evaluationReader.GetTypeSpecification(handle)
			.DecodeSignature(new RuntimeTypeSignatureProvider(this), genericContext: null).RuntimeType
			?? throw new TypeLoadException("The type specification does not identify a runtime type");
	}

	private ResolvedRuntimeType ResolveTypeReference(TypeReferenceHandle handle)
	{
		var reference = evaluationReader.GetTypeReference(handle);
		var name = evaluationReader.GetString(reference.Name);
		if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
		{
			var containing = ResolveTypeReference((TypeReferenceHandle)reference.ResolutionScope);
			var reader = containing.Module.MetadataReader.PeMetadataReader;
			foreach (var nestedHandle in reader.GetTypeDefinition(containing.Handle).GetNestedTypes())
			{
				if (reader.GetString(reader.GetTypeDefinition(nestedHandle).Name) == name)
				{
					return new ResolvedRuntimeType(containing.Module, nestedHandle);
				}
			}
			throw new TypeLoadException($"Nested type '{name}' was not found in '{GetTypeName(containing)}'");
		}

		var @namespace = evaluationReader.GetString(reference.Namespace);
		if (reference.ResolutionScope.Kind == HandleKind.AssemblyReference)
		{
			var assemblyName = evaluationReader.GetString(evaluationReader.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope).Name);
			foreach (var module in FindModules((AssemblyReferenceHandle)reference.ResolutionScope))
			{
				if (TryFindTypeInModule(module, name, @namespace, out var typeHandle))
				{
					return new ResolvedRuntimeType(module, typeHandle);
				}
			}
			throw new TypeLoadException($"Type '{@namespace}.{name}' from assembly '{assemblyName}' is not loaded");
		}

		foreach (var module in FindModules(assemblyName: null))
		{
			if (TryFindTypeInModule(module, name, @namespace, out var typeHandle))
			{
				return new ResolvedRuntimeType(module, typeHandle);
			}
		}
		throw new TypeLoadException($"Type '{@namespace}.{name}' is not loaded");
	}

	private static bool TryFindTypeInModule(ModuleInfo module, string name, string @namespace, out TypeDefinitionHandle handle)
	{
		var reader = module.MetadataReader.PeMetadataReader;
		foreach (var typeHandle in reader.TypeDefinitions)
		{
			var type = reader.GetTypeDefinition(typeHandle);
			if (reader.GetString(type.Name) == name && reader.GetString(type.Namespace) == @namespace)
			{
				handle = typeHandle;
				return true;
			}
		}
		handle = default;
		return false;
	}

	/// <summary>
	/// Resolves an assembly reference from the evaluation assembly to a loaded module, preferring the module the
	/// evaluation was compiled against (the frame module for frame evaluations, or the root value's module for
	/// DebuggerDisplay evaluations). When several modules share the same assembly identity (e.g. the same assembly
	/// loaded into multiple AssemblyLoadContexts) the preferred module is the instance the user is actually
	/// debugging, and the one Roslyn bound the expression against (see <c>CilExpressionCompiler.GetMetadataBlocks</c>).
	/// </summary>
	private IEnumerable<ModuleInfo> FindModules(AssemblyReferenceHandle assemblyReference)
	{
		var reference = evaluationReader.GetAssemblyReference(assemblyReference);
		var name = evaluationReader.GetString(reference.Name);
		var version = reference.Version;
		var culture = evaluationReader.GetString(reference.Culture);
		var publicKeyOrToken = reference.PublicKeyOrToken.IsNil ? null : evaluationReader.GetBlobBytes(reference.PublicKeyOrToken);

		var identityMatches = new List<ModuleInfo>();
		ModuleInfo? preferredIdentityMatch = null;
		foreach (var module in debugger.AllModules)
		{
			var reader = module.MetadataReader.PeMetadataReader;
			if (!reader.IsAssembly) continue;
			var assembly = reader.GetAssemblyDefinition();
			if (reader.GetString(assembly.Name) != name) continue;
			if (!MatchesAssemblyIdentity(reader, assembly, version, culture, publicKeyOrToken)) continue;
			if (module == currentFrameModule) preferredIdentityMatch = module;
			else identityMatches.Add(module);
		}
		if (preferredIdentityMatch is not null) yield return preferredIdentityMatch;
		foreach (var module in identityMatches) yield return module;

		// The referenced identity matches no loaded module (e.g. a binding redirect or a version mismatch), so fall
		// back to simple-name matching, still preferring the frame's module.
		if (identityMatches.Count == 0 && preferredIdentityMatch is null)
		{
			foreach (var module in FindModules(name)) yield return module;
		}
	}

	private static bool MatchesAssemblyIdentity(MetadataReader reader, AssemblyDefinition assembly, Version? version, string culture, byte[]? publicKeyOrToken)
	{
		if (version is not null && assembly.Version is not null && version != assembly.Version) return false;
		if (!string.Equals(culture, reader.GetString(assembly.Culture), StringComparison.OrdinalIgnoreCase)) return false;
		return PublicKeysMatch(publicKeyOrToken, assembly.PublicKey.IsNil ? null : reader.GetBlobBytes(assembly.PublicKey));
	}

	private static bool PublicKeysMatch(byte[]? referencedToken, byte[]? definitionKey)
	{
		if (referencedToken is null or { Length: 0 } || definitionKey is null or { Length: 0 }) return true;
		if (referencedToken.Length == 8 && definitionKey.Length > 8)
		{
			// The AssemblyRef carries the public key token (the last 8 bytes of the SHA1 of the full public key).
			var hash = SHA1.HashData(definitionKey);
			hash.AsSpan().Reverse();
			var matches = referencedToken.AsSpan().SequenceEqual(hash.AsSpan(0, 8));
			return matches;
		}
		return referencedToken.AsSpan().SequenceEqual(definitionKey);
	}

	private IEnumerable<ModuleInfo> FindModules(string? assemblyName)
	{
		var others = new List<ModuleInfo>();
		foreach (var module in debugger.AllModules)
		{
			var reader = module.MetadataReader.PeMetadataReader;
			if (assemblyName is not null && (reader.IsAssembly == false || reader.GetString(reader.GetAssemblyDefinition().Name) != assemblyName))
			{
				continue;
			}
			if (module == currentFrameModule) yield return module;
			else others.Add(module);
		}
		foreach (var module in others) yield return module;
	}

	private ResolvedRuntimeType FindRuntimeType(string @namespace, string name)
	{
		foreach (var module in FindModules(null))
		{
			var reader = module.MetadataReader.PeMetadataReader;
			foreach (var handle in reader.TypeDefinitions)
			{
				var type = reader.GetTypeDefinition(handle);
				if (reader.GetString(type.Namespace) == @namespace && reader.GetString(type.Name) == name)
				{
					return new ResolvedRuntimeType(module, handle);
				}
			}
		}
		throw new TypeLoadException($"Type '{@namespace}.{name}' is not loaded");
	}

	internal static bool IsValueType(ResolvedRuntimeType type)
	{
		var reader = type.Module.MetadataReader.PeMetadataReader;
		var definition = reader.GetTypeDefinition(type.Handle);
		return definition.BaseType.Kind switch
		{
			// Core-library types (e.g. in System.Private.CoreLib) define System.ValueType / System.Enum in the same
			// module, so the base type is a TypeDefinition rather than a TypeReference.
			HandleKind.TypeDefinition => IsSystemValueType(reader, reader.GetTypeDefinition((TypeDefinitionHandle)definition.BaseType)),
			HandleKind.TypeReference => IsSystemValueType(reader, reader.GetTypeReference((TypeReferenceHandle)definition.BaseType)),
			_ => false
		};
	}

	private static bool IsSystemValueType(MetadataReader reader, TypeDefinition baseType) =>
		IsSystemValueType(reader, baseType.Namespace, baseType.Name);

	private static bool IsSystemValueType(MetadataReader reader, TypeReference baseType) =>
		IsSystemValueType(reader, baseType.Namespace, baseType.Name);

	private static bool IsSystemValueType(MetadataReader reader, StringHandle @namespace, StringHandle name) =>
		reader.GetString(@namespace) == "System" && reader.GetString(name) is "ValueType" or "Enum";

	private static string GetTypeName(ResolvedRuntimeType type)
	{
		var reader = type.Module.MetadataReader.PeMetadataReader;
		var definition = reader.GetTypeDefinition(type.Handle);
		var name = reader.GetString(definition.Name);
		var declaringType = definition.GetDeclaringType();
		if (!declaringType.IsNil)
		{
			return $"{GetTypeName(new ResolvedRuntimeType(type.Module, declaringType))}+{name}";
		}
		var @namespace = reader.GetString(definition.Namespace);
		return string.IsNullOrEmpty(@namespace) ? name : $"{@namespace}.{name}";
	}

	private string GetReflectionTypeName(ResolvedCilType type)
	{
		if (type.ElementType is not null)
		{
			var suffix = type.IsSzArray ? "[]" : $"[{new string(',', type.ArrayRank - 1)}]";
			return GetReflectionTypeName(type.ElementType) + suffix;
		}
		if (type.Primitive is { } primitive) return GetPrimitiveTypeName(primitive);
		var runtimeType = type.RuntimeType ?? throw new TypeLoadException("The CIL type is unresolved");
		var name = GetTypeName(runtimeType);
		if (!runtimeType.TypeArguments.IsDefaultOrEmpty)
		{
			name += $"[[{string.Join("],[", runtimeType.TypeArguments.Select(GetAssemblyQualifiedTypeName))}]]";
		}
		return name;
	}

	private string GetTypeAssemblyName(ResolvedCilType type)
	{
		if (type.ElementType is not null) return GetTypeAssemblyName(type.ElementType);
		if (type.Primitive is not null) return "System.Private.CoreLib";
		var runtimeType = type.RuntimeType ?? throw new TypeLoadException("The CIL type is unresolved");
		var reader = runtimeType.Module.MetadataReader.PeMetadataReader;
		return reader.IsAssembly ? reader.GetString(reader.GetAssemblyDefinition().Name) : Path.GetFileNameWithoutExtension(runtimeType.Module.Module.Name);
	}

	private static string GetPrimitiveTypeName(PrimitiveTypeCode primitive) => primitive switch
	{
		PrimitiveTypeCode.Boolean => "System.Boolean",
		PrimitiveTypeCode.Byte => "System.Byte",
		PrimitiveTypeCode.SByte => "System.SByte",
		PrimitiveTypeCode.Char => "System.Char",
		PrimitiveTypeCode.Int16 => "System.Int16",
		PrimitiveTypeCode.UInt16 => "System.UInt16",
		PrimitiveTypeCode.Int32 => "System.Int32",
		PrimitiveTypeCode.UInt32 => "System.UInt32",
		PrimitiveTypeCode.Int64 => "System.Int64",
		PrimitiveTypeCode.UInt64 => "System.UInt64",
		PrimitiveTypeCode.Single => "System.Single",
		PrimitiveTypeCode.Double => "System.Double",
		PrimitiveTypeCode.String => "System.String",
		PrimitiveTypeCode.Object => "System.Object",
		PrimitiveTypeCode.IntPtr => "System.IntPtr",
		PrimitiveTypeCode.UIntPtr => "System.UIntPtr",
		_ => throw new NotSupportedException($"Primitive type '{primitive}' is not supported")
	};

	private static bool SignaturesEqual(MethodSignature<string> left, MethodSignature<string> right)
	{
		return left.GenericParameterCount == right.GenericParameterCount &&
			left.ParameterTypes.Length == right.ParameterTypes.Length &&
			left.ParameterTypes.Zip(right.ParameterTypes).All(types => TypesCompatible(types.First, types.Second)) &&
			TypesCompatible(left.ReturnType, right.ReturnType);
	}

	private static bool TypesCompatible(string expected, string actual) => expected == actual;

	private sealed class SignatureNameProvider : ISignatureTypeProvider<string, object?>
	{
		public static SignatureNameProvider Instance { get; } = new();
		public string GetArrayType(string elementType, ArrayShape shape) => $"{elementType}[{new string(',', shape.Rank - 1)}]";
		public string GetByReferenceType(string elementType) => elementType + "&";
		public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
		public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => $"{genericType}<{string.Join(",", typeArguments)}>";
		public string GetGenericMethodParameter(object? genericContext, int index) => $"!!{index}";
		public string GetGenericTypeParameter(object? genericContext, int index) => $"!{index}";
		public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
		public string GetPinnedType(string elementType) => elementType;
		public string GetPointerType(string elementType) => elementType + "*";
		public string GetSZArrayType(string elementType) => elementType + "[]";
		public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
		public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => GetFullName(reader, handle);
		public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => GetFullName(reader, handle);
		public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
			reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

		private static string GetFullName(MetadataReader reader, TypeDefinitionHandle handle)
		{
			var type = reader.GetTypeDefinition(handle);
			var ns = reader.GetString(type.Namespace);
			var name = reader.GetString(type.Name);
			return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
		}

		private static string GetFullName(MetadataReader reader, TypeReferenceHandle handle)
		{
			var type = reader.GetTypeReference(handle);
			var name = reader.GetString(type.Name);
			if (type.ResolutionScope.Kind == HandleKind.TypeReference) return $"{GetFullName(reader, (TypeReferenceHandle)type.ResolutionScope)}+{name}";
			var ns = reader.GetString(type.Namespace);
			return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
		}
	}

	private sealed class RuntimeTypeSignatureProvider(EvaluationMetadataResolver resolver) : ISignatureTypeProvider<ResolvedCilType, object?>
	{
		public ResolvedCilType GetPrimitiveType(PrimitiveTypeCode typeCode) => new(typeCode, null);
		public ResolvedCilType GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
			new(null, resolver.ResolveType(handle));
		public ResolvedCilType GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
			new(null, resolver.ResolveType(handle));
		public ResolvedCilType GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
			reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
		public ResolvedCilType GetGenericInstantiation(ResolvedCilType genericType, ImmutableArray<ResolvedCilType> typeArguments) =>
			genericType.RuntimeType is { } runtimeType
				? genericType with { RuntimeType = runtimeType with { TypeArguments = typeArguments } }
				: throw new TypeLoadException("A generic instantiation must identify a runtime type");
		public ResolvedCilType GetArrayType(ResolvedCilType elementType, ArrayShape shape) => new(null, null, elementType, shape.Rank);
		public ResolvedCilType GetSZArrayType(ResolvedCilType elementType) => new(null, null, elementType, 1, true);
		public ResolvedCilType GetByReferenceType(ResolvedCilType elementType) => elementType;
		public ResolvedCilType GetPointerType(ResolvedCilType elementType) => elementType;
		public ResolvedCilType GetPinnedType(ResolvedCilType elementType) => elementType;
		public ResolvedCilType GetModifiedType(ResolvedCilType modifier, ResolvedCilType unmodifiedType, bool isRequired) => unmodifiedType;
		public ResolvedCilType GetFunctionPointerType(MethodSignature<ResolvedCilType> signature) => throw new NotSupportedException("Function pointer types are not supported");
		public ResolvedCilType GetGenericMethodParameter(object? genericContext, int index) => resolver.ResolveGenericMethodParameter(index);
		public ResolvedCilType GetGenericTypeParameter(object? genericContext, int index) => resolver.ResolveGenericTypeParameter(index);
	}
}
