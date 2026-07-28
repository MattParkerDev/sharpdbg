using System.Collections.Immutable;
using ICorDebugSharp;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using SharpDbg.Infrastructure.Debugger.ExpressionEvaluator.Interpreter;

namespace SharpDbg.Infrastructure.Debugger;

public partial class ManagedDebugger
{
	// e.g. localVar, or localVar.Field1.Field2, or ClassName.StaticField.SubField
	// optionalInputValue may be provided, e.g. in the case of where the value was created in the evaluation and does not exist
	// as a local in the stack frame.
	internal async Task<(ICorDebugValue Value, bool IsType, SetterData? SetterData)> ResolveIdentifiers(List<string> identifiers, ThreadId threadId, FrameStackDepth stackDepth, ICorDebugValue? optionalInputValue, ICorDebugValue? optionalRootValue, ICorDebugType[]? genericTypeArguments = null)
	{
		if (identifiers.Count is 0)
		{
			if (optionalInputValue is not null) return (optionalInputValue, false, null);
			throw new ArgumentException("Identifiers list cannot be empty", nameof(identifiers));
		}
		if (optionalInputValue is not null && optionalRootValue is not null) throw new ArgumentException("Cannot provide both an input value and a root value");

		var rootValue = optionalInputValue;
		int? nextIdentifier = null;
		var isType = false;
		SetterData? setterData = null;
		if (rootValue is null)
		{
			(rootValue, nextIdentifier, isType, setterData) = await ResolveFirstIdentifier(identifiers, threadId, stackDepth, optionalRootValue, genericTypeArguments);
			if (rootValue is null) throw new InvalidOperationException("Identifier value is null. Even if the identifier could not be resolved, an exception should have been thrown, returned as the CorDebugValue");
		}

		foreach (var identifier in identifiers.Skip(nextIdentifier ?? 1))
		{
			var member = await ResolveIdentifierAsMember(identifier, threadId, stackDepth, rootValue!);
			rootValue = member?.Value;
			setterData = member?.SetterData;
			isType = false;
		}
		if (rootValue is null) throw new InvalidOperationException("Final resolved identifier value is null. Even if the identifier could not be resolved, an exception should have been thrown, returned as the CorDebugValue");
		return (rootValue, isType, setterData);
	}

	// Only takes the full list as resolving it as a static class needs to e.g. search through namespaces
	// We must return the next identifier index to process after the static class name
	// An optional root value is supplied if the identifiers should be resolved against it only, e.g. for DebuggerDisplay expressions
	private async Task<(ICorDebugValue Value, int? NextIdentifier, bool IsType, SetterData? SetterData)> ResolveFirstIdentifier(List<string> identifiers, ThreadId threadId, FrameStackDepth stackDepth, ICorDebugValue? optionalRootValue, ICorDebugType[]? genericTypeArguments)
	{
		var firstIdentifier = identifiers[0];
		ArgumentException.ThrowIfNullOrWhiteSpace(firstIdentifier);
		// Try
		// 1. Stack variable, e.g. local variable or argument
		// 2. Field or property of 'this' if available (instance or static)
		// 3. Identifier as static class name
		ICorDebugValue? resolvedValue = null;
		ICorDebugValue? instanceMethodImplicitThisValue = optionalRootValue;

		if (optionalRootValue is null) resolvedValue = ResolveIdentifierAsStackVariable(firstIdentifier, threadId, stackDepth, out instanceMethodImplicitThisValue);
		if (resolvedValue is not null) return (resolvedValue, null, false, null);
		if (instanceMethodImplicitThisValue is not null)
		{
			var member = await ResolveIdentifierAsMember(firstIdentifier, threadId, stackDepth, instanceMethodImplicitThisValue);
			if (member is not null) return (member.Value.Value, null, false, member.Value.SetterData);
		}
		var result = await ResolveStaticClassFromIdentifiers(identifiers, threadId, stackDepth, genericTypeArguments);
		resolvedValue = result?.Value;
		if (resolvedValue is not null) return (resolvedValue, result!.Value.NextIdentifier, true, null);

		throw new InvalidOperationException($"Could not resolve identifier '{firstIdentifier}' as a stack variable.");
	}

	private ICorDebugValue? ResolveIdentifierAsStackVariable(string identifier, ThreadId threadId, FrameStackDepth stackDepth, out ICorDebugValue? instanceMethodImplicitThisValue)
	{
		instanceMethodImplicitThisValue = null;

		if (identifier == "$exception")
		{
			var currentThread = _process!.GetThread(threadId.Value);
			return currentThread.CurrentException;
		}

		var frame = GetFrameForThreadIdAndStackDepth(threadId, stackDepth);
		var corDebugFunction = frame.Function;
		var module = _modules[corDebugFunction.Module.BaseAddress];
		var currentIlOffset = frame.IP.pnOffset;

		foreach (var (index, local) in frame.LocalVariables.Index())
		{
			var localVariableName = module.MetadataReader.GetLocalVariableName(corDebugFunction.Token, index, currentIlOffset);
			if (localVariableName is null) continue; // Compiler generated locals will not be found. E.g. DefaultInterpolatedStringHandler
			if (localVariableName == identifier)
			{
				return local;
			}
		}

		var metadataImport = module.Module.GetMetaDataInterface<IMetaDataImport>();
		var methodProps = metadataImport!.GetMethodProps(corDebugFunction.Token);
		var isStatic = methodProps.pdwAttr.IsMdStatic();
		var methodName = methodProps.szMethod;
		var implicitThisValue = frame.Arguments[0];

		// Async state machines and lambdas: locals are hoisted into fields on the compiler-generated class.
		// Detect this early so we can search those fields to find the identifier, or if we don't, set instanceMethodImplicitThisValue to the
		// real user 'this' rather than the raw state-machine object.
		if (isStatic is false && (methodName is "MoveNext" || methodName.Contains(">b__")))
		{
			var containingClassName = metadataImport.GetTypeDefProps(corDebugFunction.Class.Token).szTypeDef;
			var classGeneratedNameKind = GeneratedNameParser.GetKind(containingClassName);
			if (classGeneratedNameKind is GeneratedNameKind.StateMachineType or GeneratedNameKind.LambdaDisplayClass)
			{
				// Search hoisted fields on the state machine / closure chain for the identifier
				var hoistedValue = FindHoistedLocalInClosureChain(implicitThisValue, identifier, metadataImport);
				if (hoistedValue is not null) return hoistedValue;

				// Set instanceMethodImplicitThisValue to the real user 'this' (not the state machine)
				// so that ResolveIdentifierAsMember can search it for fields/properties.
				// May be null for static async methods.
				instanceMethodImplicitThisValue = GetAsyncOrLambdaProxyFieldValue(implicitThisValue, metadataImport);
				return null;
			}
		}

		// Instance methods: Arguments[0] == "this"
		if (!isStatic)
		{
			if (identifier == "this")
			{
				return implicitThisValue;
			}
		}

		var skipCount = isStatic ? 0 : 1;

		foreach (var (index, argumentValue) in frame.Arguments.Skip(skipCount).Index())
		{
			// index 0 is the return value, so we add 1 to get to the arguments
			var paramDef = metadataImport.GetParamForMethodIndex(corDebugFunction.Token, index + 1);
			var paramProps = metadataImport.GetParamProps(paramDef);
			var argumentName = paramProps.szName;
			if (argumentName is null) continue;

			if (argumentName == identifier)
			{
				return argumentValue;
			}
		}

		// If we're here, we didn't find the identifier as a local, so lets set instanceMethodImplicitThisValue so the call can try to resolve the identifier against it, rather than against the async state machine or lambda DisplayClass
		if (isStatic is false)
		{
			instanceMethodImplicitThisValue = implicitThisValue;
		}

		return null;
	}

	/// Searches the closure chain starting at <paramref name="closureValue"/> for a hoisted local
	/// whose original name matches <paramref name="identifier"/>. Walks parent closures linked via
	/// <see cref="GeneratedNameKind.DisplayClassLocalOrField"/> fields.
	private static ICorDebugValue? FindHoistedLocalInClosureChain(ICorDebugValue closureValue, string identifier, IMetaDataImport metadataImport)
	{
		var objectValue = closureValue.UnwrapDebugValueToObject();
		var fields = metadataImport.EnumFields(objectValue.Class.Token);
		foreach (var field in fields)
		{
			var fieldProps = metadataImport.GetFieldProps(field);
			var fieldName = fieldProps.szField;
			GeneratedNameParser.TryParseGeneratedName(fieldName, out var kind, out var openBracketOffset, out var closeBracketOffset);
			if (kind is GeneratedNameKind.HoistedLocalField)
			{
				var originalName = fieldName.AsSpan()[(openBracketOffset + 1)..closeBracketOffset].ToString();
				if (originalName == identifier) return objectValue.GetFieldValue(objectValue.Class, field);
			}
			else if (kind is GeneratedNameKind.DisplayClassLocalOrField)
			{
				var parentClosureValue = objectValue.GetFieldValue(objectValue.Class, field);
				var parentObjectValue = parentClosureValue.UnwrapDebugValueToObject();
				var parentMetadata = parentObjectValue.Class.Module.GetMetaDataInterface<IMetaDataImport>();
				var result = FindHoistedLocalInClosureChain(parentClosureValue, identifier, parentMetadata);
				if (result is not null) return result;
			}
		}
		return null;
	}

	private async Task<(ICorDebugValue Value, SetterData? SetterData)?> ResolveIdentifierAsMember(string identifier, ThreadId threadId, FrameStackDepth stackDepth, ICorDebugValue instanceMethodImplicitThisValue)
	{
		var unwrappedThisValue = instanceMethodImplicitThisValue.UnwrapDebugValueToObject();
		var frame = GetFrameForThreadIdAndStackDepth(threadId, stackDepth);
		var fieldValue = unwrappedThisValue.GetClassFieldValue(frame, identifier);
		if (fieldValue is not null) return (fieldValue, null);

		var property = await instanceMethodImplicitThisValue.GetPropertyValueWithSetter(_callbacks, EvalStatus, frame, identifier);
		if (property is not null)
		{
			return (property.Value.Value, new SetterData
			{
				OwnerValue = instanceMethodImplicitThisValue,
				SetterFunction = property.Value.SetterFunction
			});
		}
		return null;
	}

	private async Task<(ICorDebugValue Value, int NextIdentifier)?> ResolveStaticClassFromIdentifiers(List<string> identifiers, ThreadId threadId, FrameStackDepth stackDepth, ICorDebugType[]? genericTypeArguments)
	{
		// First, try to resolve using imported namespaces from the current method's PDB symbols
		var typeTokenResult = FindTypeTokenInLoadedModulesWithNamespaceHints(identifiers, threadId, stackDepth);

		if (typeTokenResult is null) return null;

		var (module, typeToken, nextIdentifier) = typeTokenResult.Value;
		var corDebugClass = module.Module.GetClassFromToken(typeToken);
		var classValue = await CreateTypeObjectStaticConstructor(corDebugClass, genericTypeArguments ?? [], threadId, stackDepth);
		if (classValue is null) return null;
		return (classValue, nextIdentifier);
	}

	private (ModuleInfo moduleInfo, mdTypeDef typeToken, int nextIdentifier)? FindTypeTokenInLoadedModulesWithNamespaceHints(List<string> identifiers, ThreadId threadId, FrameStackDepth stackDepth)
	{
		if (identifiers.Count == 0) return null;

		var frame = GetFrameForThreadIdAndStackDepth(threadId, stackDepth);
		var corDebugFunction = frame.Function;
		var currentModule = _modules[corDebugFunction.Module.BaseAddress];

		var importedNamespaces = currentModule.MetadataReader.GetImportedNamespaces(corDebugFunction.Token);

		if (importedNamespaces.Length is 0) return null;
		var result = FindTypeTokenInLoadedModules(identifiers, importedNamespaces);
		return result;
	}

	private async Task<ICorDebugValue?> CreateTypeObjectStaticConstructor(ICorDebugClass corDebugClass, ICorDebugType[] genericTypeArguments, ThreadId threadId, FrameStackDepth stackDepth)
	{
		var ilFrame = GetFrameForThreadIdAndStackDepth(threadId, stackDepth);
		var eval = ilFrame.Chain.Thread.CreateEval();
		var value = await eval.NewParameterizedObjectNoConstructorAsync(_callbacks, EvalStatus, corDebugClass, genericTypeArguments.Length, genericTypeArguments);
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

	private (mdTypeDef typeToken, int nextIdentifier)? FindTypeTokenInModule(ICorDebugModule module, List<string> identifiers, ImmutableArray<string> importedNamespaces)
	{
		var metadataImport = module.GetMetaDataInterface<IMetaDataImport>();
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
		if (typeName.Contains('`')) return typeName;

		var angleBracketIndex = typeName.IndexOf('<');
		if (angleBracketIndex < 0) return typeName;

		var arity = 1;
		var depth = 0;
		foreach (var character in typeName.AsSpan()[(angleBracketIndex + 1)..^1])
		{
			if (character == '<') depth++;
			else if (character == '>') depth--;
			else if (character == ',' && depth == 0) arity++;
		}
		return $"{typeName[..angleBracketIndex]}`{arity}";
	}
}
