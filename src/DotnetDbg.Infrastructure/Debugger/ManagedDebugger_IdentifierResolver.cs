using System.Collections.Immutable;
using ClrDebug;

namespace DotnetDbg.Infrastructure.Debugger;

public partial class ManagedDebugger
{
	// e.g. localVar, or localVar.Field1.Field2, or ClassName.StaticField.SubField
	// optionalInputValue may be provided, e.g. in the case of where the value was created in the evaluation and does not exist
	// as a local in the stack frame.
	public async Task<CorDebugValue> ResolveIdentifiers(List<string> identifiers, ThreadId threadId, FrameStackDepth stackDepth, CorDebugValue? optionalInputValue, CorDebugValue? optionalRootValue)
	{
		if (identifiers.Count is 0)
		{
			if (optionalInputValue is not null) return optionalInputValue;
			throw new ArgumentException("Identifiers list cannot be empty", nameof(identifiers));
		}
		if (optionalInputValue is not null && optionalRootValue is not null) throw new ArgumentException("Cannot provide both an input value and a root value");

		var rootValue = optionalInputValue;
		int? nextIdentifier = null;
		if (rootValue is null)
		{
			(rootValue, nextIdentifier) = await ResolveFirstIdentifier(identifiers, threadId, stackDepth, optionalRootValue);
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
	// An optional root value is supplied if the identifiers should be resolved against it only, e.g. for DebuggerDisplay expressions
	private async Task<(CorDebugValue Value, int? NextIdentifier)> ResolveFirstIdentifier(List<string> identifiers, ThreadId threadId, FrameStackDepth stackDepth, CorDebugValue? optionalRootValue)
	{
		var firstIdentifier = identifiers[0];
		ArgumentException.ThrowIfNullOrWhiteSpace(firstIdentifier);
		// Try
		// 1. Stack variable, e.g. local variable or argument
		// 2. Field or property of 'this' if available (instance or static)
		// 3. Identifier as static class name
		CorDebugValue? resolvedValue = null;
		CorDebugValue? instanceMethodImplicitThisValue = optionalRootValue;

		if (optionalRootValue is null) resolvedValue = ResolveIdentifierAsStackVariable(firstIdentifier, threadId, stackDepth, out instanceMethodImplicitThisValue);
		if (resolvedValue is not null) return (resolvedValue, null);
		if (instanceMethodImplicitThisValue is not null) resolvedValue = await ResolveIdentifierAsMember(firstIdentifier, threadId, stackDepth, instanceMethodImplicitThisValue);
		if (resolvedValue is not null) return (resolvedValue, null);
		var result = await ResolveStaticClassFromIdentifiers(identifiers, threadId, stackDepth);
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

	private async Task<(CorDebugValue Value, int NextIdentifier)?> ResolveStaticClassFromIdentifiers(List<string> identifiers, ThreadId threadId, FrameStackDepth stackDepth)
	{
		// First, try to resolve using imported namespaces from the current method's PDB symbols
		var typeTokenResult = FindTypeTokenInLoadedModulesWithNamespaceHints(identifiers, threadId, stackDepth);

		if (typeTokenResult is null) return null;

		var (module, typeToken, nextIdentifier) = typeTokenResult.Value;
		var corDebugClass = module.Module.GetClassFromToken(typeToken);
		var classValue = await CreateTypeObjectStaticConstructor(corDebugClass, threadId, stackDepth);
		if (classValue is null) return null;
		return (classValue, nextIdentifier);
	}

	private (ModuleInfo moduleInfo, mdTypeDef typeToken, int nextIdentifier)? FindTypeTokenInLoadedModulesWithNamespaceHints(List<string> identifiers, ThreadId threadId, FrameStackDepth stackDepth)
	{
		if (identifiers.Count == 0) return null;

		var frame = GetFrameForThreadIdAndStackDepth(threadId, stackDepth);
		var corDebugFunction = frame.Function;
		var currentModule = _modules[corDebugFunction.Module.BaseAddress];

		var importedNamespaces = currentModule.SymbolReader?.GetImportedNamespaces(corDebugFunction.Token) ?? ImmutableArray<string>.Empty;

		if (importedNamespaces.Length is 0) return null;
		var result = FindTypeTokenInLoadedModules(identifiers, importedNamespaces);
		return result;
	}

	private async Task<CorDebugValue?> CreateTypeObjectStaticConstructor(CorDebugClass corDebugClass, ThreadId threadId, FrameStackDepth stackDepth)
	{
		var ilFrame = GetFrameForThreadIdAndStackDepth(threadId, stackDepth);
		var eval = ilFrame.Chain.Thread.CreateEval();
		// currently only working for non-generic classes
		var value = await eval.NewParameterizedObjectNoConstructorAsync(_callbacks, corDebugClass, 0, null);
		return value;
	}

	private (ModuleInfo moduleInfo, mdTypeDef typeToken, int nextIdentifier)? FindTypeTokenInLoadedModules(List<string> identifiers, ImmutableArray<string> importedNamespaces)
	{
		foreach (var module in _modules.Values)
		{
			var result = FindTypeTokenInModule(module.Module, identifiers, importedNamespaces);
			if (result is not null)
			{
				return (module, result.Value.typeToken, result.Value.nextIdentifier);
			}
		}
		return null;
	}

	private (mdTypeDef typeToken, int nextIdentifier)? FindTypeTokenInModule(CorDebugModule module, List<string> identifiers, ImmutableArray<string> importedNamespaces)
	{
		var metadataImport = module.GetMetaDataInterface().MetaDataImport;
		mdTypeDef? typeToken = null;

		string currentTypeName = string.Empty;
		var nextIdentifier = 0;

		for (int i = nextIdentifier; i < identifiers.Count; i++)
		{
			string name = ParseGenericParams(identifiers[i]);
			currentTypeName += (string.IsNullOrEmpty(currentTypeName) ? "" : ".") + name;

			typeToken = metadataImport.FindTypeDefByNameOrNullInCandidateNamespaces(currentTypeName, mdToken.Nil, importedNamespaces);
			if (typeToken is not null)
			{
				nextIdentifier = i + 1;
				break;
			}
		}

		if (typeToken is null)
			return null;

		for (int j = nextIdentifier; j < identifiers.Count; j++)
		{
			string name = ParseGenericParams(identifiers[j]);
			var classToken = metadataImport.FindTypeDefByNameOrNull(name, typeToken.Value);
			if (classToken is null)
				break;
			typeToken = classToken;
			nextIdentifier = j + 1;
		}

		return (typeToken.Value, nextIdentifier);
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
