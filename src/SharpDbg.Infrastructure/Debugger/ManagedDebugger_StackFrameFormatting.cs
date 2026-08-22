using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using ICorDebugSharp;

namespace SharpDbg.Infrastructure.Debugger;

public partial class ManagedDebugger
{
	/// <summary>'Module.dll!Namespace.Type.Method&lt;T&gt;(ParamType paramName, ...)'.</summary>
	private string GetFunctionFormattedName(ICorDebugILFrame frame, int? asyncKickoffMethodToken = null)
	{
		try
		{
			var function = frame.Function;
			var token = asyncKickoffMethodToken ?? function.Token;
			var module = function.Module;
			var reader = _modules[module.BaseAddress].MetadataReader.PeMetadataReader;
			var methodDefinition = reader.GetMethodDefinition((MethodDefinitionHandle)MetadataTokens.Handle(token));
			var declaringTypeHandle = methodDefinition.GetDeclaringType();
			var typeArgumentCount = reader.GetTypeDefinition(declaringTypeHandle).GetGenericParameters().Count;
			var methodArgumentCount = methodDefinition.GetGenericParameters().Count;
			var runtimeArguments = frame.TypeParameters;
			if (runtimeArguments.Length < typeArgumentCount + methodArgumentCount)
			{
				throw new InvalidOperationException("The frame did not provide all generic type arguments.");
			}
			var typeArgumentNames = runtimeArguments
				.Take(typeArgumentCount)
				.Select(GetCorDebugTypeFriendlyName)
				.ToList();
			var methodArgumentNames = runtimeArguments
				.Skip(typeArgumentCount)
				.Take(methodArgumentCount)
				.Select(GetCorDebugTypeFriendlyName)
				.ToList();

			var genericContext = new FrameDisplayGenericContext(typeArgumentNames, methodArgumentNames);
			var className = GetFormattedTypeName(reader, declaringTypeHandle, typeArgumentNames);
			var methodName = reader.GetString(methodDefinition.Name);
			if (methodArgumentNames.Count > 0) methodName += $"<{string.Join(", ", methodArgumentNames)}>";
			var parameters = GetFormattedParameters(reader, methodDefinition, genericContext);
			return $"{Path.GetFileName(module.Name)}!{className}.{methodName}({parameters})";
		}
		catch
		{
			return "Unknown";
		}
	}

	private static string GetFormattedParameters(MetadataReader reader, MethodDefinition methodDefinition, FrameDisplayGenericContext genericContext)
	{
		var parameterTypes = methodDefinition.DecodeSignature(FrameDisplaySignatureTypeProvider.Instance, genericContext).ParameterTypes;
		var parameterNames = methodDefinition.GetParameters()
			.Select(reader.GetParameter)
			.Where(parameter => parameter.SequenceNumber > 0)
			.ToDictionary(parameter => parameter.SequenceNumber, parameter => reader.GetString(parameter.Name));
		return string.Join(", ", parameterTypes.Select((type, index) =>
			parameterNames.TryGetValue(index + 1, out var name) && name.Length > 0 ? $"{type} {name}" : type));
	}

	private static string GetFormattedTypeName(MetadataReader reader, TypeDefinitionHandle handle, IReadOnlyList<string> typeArguments)
	{
		var argumentIndex = 0;
		return GetFormattedTypeName(reader, handle, typeArguments, ref argumentIndex);
	}

	private static string GetFormattedTypeName(MetadataReader reader, TypeDefinitionHandle handle, IReadOnlyList<string> typeArguments, ref int argumentIndex)
	{
		var type = reader.GetTypeDefinition(handle);
		var declaringType = type.GetDeclaringType();
		var prefix = declaringType.IsNil
			? reader.GetString(type.Namespace)
			: GetFormattedTypeName(reader, declaringType, typeArguments, ref argumentIndex);
		var name = reader.GetString(type.Name);
		var arityIndex = name.LastIndexOf('`');
		if (arityIndex >= 0 && int.TryParse(name.AsSpan(arityIndex + 1), out var arity))
		{
			name = $"{name[..arityIndex]}<{string.Join(", ", typeArguments.Skip(argumentIndex).Take(arity))}>";
			argumentIndex += arity;
		}
		return string.IsNullOrEmpty(prefix) ? name : $"{prefix}.{name}";
	}
}
