using ClrDebug;

namespace DotnetDbg.Infrastructure.Debugger;

public partial class ManagedDebugger
{
	// e.g. localVar, or localVar.Field1.Field2, or ClassName.StaticField.SubField
	// optionalInputValue may be provided, e.g. in the case of where the value was created in the evaluation and does not exist
	// as a local in the stack frame.
	public async Task<CorDebugValue> ResolveIdentifiers(List<string> identifiers, ThreadId threadId, FrameStackDepth stackDepth, CorDebugValue? optionalInputValue)
	{
		if (identifiers.Count is 0)
		{
			if (optionalInputValue is not null) return optionalInputValue;
			throw new ArgumentException("Identifiers list cannot be empty", nameof(identifiers));
		}
		var rootValue = optionalInputValue;
		int? nextIdentifier = null;
		if (rootValue is null)
		{
			(rootValue, nextIdentifier) = await ResolveFirstIdentifier(identifiers, threadId, stackDepth);
			if (rootValue is null) throw new InvalidOperationException("Identifier value is null. Even if the identifier could not be resolved, an exception should have been thrown, returned as the CorDebugValue");
		}

		foreach (var identifier in identifiers.Skip(nextIdentifier ?? 1))
		{
			rootValue = await ResolveIdentifierAsMember(identifier, threadId, stackDepth, rootValue!);
		}
		if (rootValue is null) throw new InvalidOperationException("Final resolved identifier value is null. Even if the identifier could not be resolved, an exception should have been thrown, returned as the CorDebugValue");
		return rootValue;
	}

	// Only takes the full list as resolving it as a static class needs to e.g. search through namespaces
	// We must return the next identifier index to process after the static class name
	private async Task<(CorDebugValue Value, int? NextIdentifier)> ResolveFirstIdentifier(List<string> identifiers, ThreadId threadId, FrameStackDepth stackDepth)
	{
		var firstIdentifier = identifiers[0];
		ArgumentException.ThrowIfNullOrWhiteSpace(firstIdentifier);
		// Try
		// 1. Stack variable, e.g. local variable or argument
		// 2. Field or property of 'this' if available (instance or static)
		// 3. Identifier as static class name
		var resolvedValue = ResolveIdentifierAsStackVariable(firstIdentifier, threadId, stackDepth, out var instanceMethodImplicitThisValue);
		if (resolvedValue is not null) return (resolvedValue, null);
		if (instanceMethodImplicitThisValue is not null) resolvedValue = await ResolveIdentifierAsMember(firstIdentifier, threadId, stackDepth, instanceMethodImplicitThisValue);
		if (resolvedValue is not null) return (resolvedValue, null);
		var result = ResolveStaticClassFromIdentifiers(identifiers, threadId, stackDepth);
		resolvedValue = result?.Value;
		if (resolvedValue is not null) return (resolvedValue, result!.Value.NextIdentifier);

		throw new InvalidOperationException($"Could not resolve identifier '{firstIdentifier}' as a stack variable.");
	}

	private CorDebugValue? ResolveIdentifierAsStackVariable(string identifier, ThreadId threadId, FrameStackDepth stackDepth, out CorDebugValue? instanceMethodImplicitThisValue)
	{
		instanceMethodImplicitThisValue = null;
		var frame = GetFrameForThreadIdAndStackDepth(threadId, stackDepth);
		var corDebugFunction = frame.Function;
		var module = _modules[corDebugFunction.Module.BaseAddress];

		foreach (var (index, local) in frame.LocalVariables.Index())
		{
			var localVariableName = module.SymbolReader?.GetLocalVariableName(corDebugFunction.Token, index);
			if (localVariableName is null) continue; // Compiler generated locals will not be found. E.g. DefaultInterpolatedStringHandler
			if (localVariableName == identifier)
			{
				return local;
			}
		}

		var metadataImport = module.Module.GetMetaDataInterface().MetaDataImport;
		var methodProps = metadataImport!.GetMethodProps(corDebugFunction.Token);
		var isStatic = (methodProps.pdwAttr & CorMethodAttr.mdStatic) != 0;

		// Instance methods: Arguments[0] == "this"
		if (!isStatic)
		{
			if (identifier == "this")
			{
				return frame.Arguments[0];
			}
		}

		var skipCount = isStatic ? 0 : 1;

		foreach (var (index, argumentValue) in frame.Arguments.Skip(skipCount).Index())
		{
			// Metadata parameter index:
			// - Metadata index starts at 1
			// - Does NOT include 'this'
	        var metadataIndex = isStatic ?  index + 1 : index;
	        // index 0 is the return value, so we add 1 to get to the arguments
			var paramDef = metadataImport.GetParamForMethodIndex(corDebugFunction.Token, metadataIndex + 1);
			var paramProps = metadataImport.GetParamProps(paramDef);
			var argumentName = paramProps.szName;
			if (argumentName is null) continue;

			if (argumentName == identifier)
			{
				return argumentValue;
			}
		}

		// if we're here, we didn't find it, so lets return the 'this' argument if it's a static instance, and we find it
		if (isStatic is false)
		{
			instanceMethodImplicitThisValue = frame.Arguments[0];
		}

		return null;
	}

	private async Task<CorDebugValue?> ResolveIdentifierAsMember(string identifier, ThreadId threadId, FrameStackDepth stackDepth, CorDebugValue instanceMethodImplicitThisValue)
	{
		var unwrappedThisValue = instanceMethodImplicitThisValue.UnwrapDebugValueToObject();
		var frame = GetFrameForThreadIdAndStackDepth(threadId, stackDepth);
		var fieldValue = unwrappedThisValue.GetClassFieldValue(frame, identifier);
		if (fieldValue is not null) return fieldValue;

		var propertyValue = await instanceMethodImplicitThisValue.GetPropertyValue(frame, _callbacks, identifier);
		if (propertyValue is not null) return propertyValue;
		return null;
	}

	private (CorDebugValue Value, int NextIdentifier)? ResolveStaticClassFromIdentifiers(List<string> identifiers, ThreadId threadId, FrameStackDepth stackDepth)
	{
		return null;
	}

	private (CorDebugValue, int NextIdentifier)? GetStaticClassCorDebugValueForIdentifiers(List<string> identifiers)
	{

	}

	private (mdTypeDef typeToken, int nextIdentifier)? FindTypeTokenInLoadedModules(List<string> identifiers)
	{
		foreach (var module in _modules.Values)
		{
			var result = FindTypeTokenInModule(module.Module, identifiers);
			if (result is not null)
			{
				return result.Value;
			}
		}
		return null;
	}

	private (mdTypeDef typeToken, int nextIdentifier)? FindTypeTokenInModule(CorDebugModule module, List<string> identifiers)
	{
		var metadataImport = module.GetMetaDataInterface().MetaDataImport;
		var typeToken = mdTypeDef.Nil;

		string currentTypeName = string.Empty;
		var nextIdentifier = 0;

		for (int i = nextIdentifier; i < identifiers.Count; i++)
		{
			string name = ParseGenericParams(identifiers[i]);
			currentTypeName += (string.IsNullOrEmpty(currentTypeName) ? "" : ".") + name;

			typeToken = metadataImport.FindTypeDefByName(currentTypeName, mdToken.Nil);
			if (!typeToken.IsNil)
			{
				nextIdentifier = i + 1;
				break;
			}
		}

		if (typeToken.IsNil)
			return null;

		for (int j = nextIdentifier; j < identifiers.Count; j++)
		{
			string name = ParseGenericParams(identifiers[j]);
			mdTypeDef classToken = metadataImport.FindTypeDefByName(name, typeToken);
			if (classToken.IsNil)
				break;
			typeToken = classToken;
			nextIdentifier = j + 1;
		}

		return (typeToken, nextIdentifier);
	}

	private static string ParseGenericParams(string typeName)
	{
		int genericIndex = typeName.IndexOf('`');
		if (genericIndex >= 0)
		{
			typeName = typeName.Substring(0, genericIndex);
		}
		int angleBracketIndex = typeName.IndexOf('<');
		if (angleBracketIndex >= 0)
		{
			typeName = typeName.Substring(0, angleBracketIndex);
		}
		return typeName;
	}
}
