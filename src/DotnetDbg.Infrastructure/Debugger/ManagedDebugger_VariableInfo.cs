using System.Diagnostics;
using ClrDebug;
using ZLinq;

namespace DotnetDbg.Infrastructure.Debugger;

public partial class ManagedDebugger
{
	private async Task AddLocalVariables(ModuleInfo module, CorDebugFunction corDebugFunction, List<VariableInfo> result, ThreadId threadId, FrameStackDepth stackDepth)
	{
		var corDebugIlFrame = GetFrameForThreadIdAndStackDepth(threadId, stackDepth);
		if (corDebugIlFrame.LocalVariables.Length is 0) return;
		foreach (var (index, localVariableCorDebugValue) in corDebugIlFrame.LocalVariables.Index())
		{
			var localVariableName = module.SymbolReader?.GetLocalVariableName(corDebugFunction.Token, index);
			if (localVariableName is null) continue; // Compiler generated locals will not be found. E.g. DefaultInterpolatedStringHandler
			var (friendlyTypeName, value, debuggerProxyInstance) = await GetValueForCorDebugValueAsync(localVariableCorDebugValue, threadId, stackDepth);
			var variableInfo = new VariableInfo
			{
				Name = localVariableName,
				Value = value,
				Type = friendlyTypeName,
				VariablesReference = GetVariablesReference(localVariableCorDebugValue, friendlyTypeName, threadId, stackDepth, debuggerProxyInstance)
			};
			result.Add(variableInfo);
		}
	}

	private async Task AddArguments(ModuleInfo module, CorDebugFunction corDebugFunction, List<VariableInfo> result, ThreadId threadId, FrameStackDepth stackDepth)
	{
		var corDebugIlFrame = GetFrameForThreadIdAndStackDepth(threadId, stackDepth);
		if (corDebugIlFrame.Arguments.Length is 0) return;
        var metadataImport = module.Module.GetMetaDataInterface().MetaDataImport;

        // localsScope.Frame.Arguments includes the implicit "this" parameter for instance methods,
        // but GetParamForMethodIndex does NOT include it - it is named by convention
        // so we need to check the method attributes to see if it's static or instance, to conditionally handle "this"
        var methodProps = metadataImport!.GetMethodProps(corDebugFunction.Token);
        var isStatic = (methodProps.pdwAttr & CorMethodAttr.mdStatic) != 0;
        if (isStatic is false)
        {
	        var implicitThisValue = corDebugIlFrame.Arguments[0];
	        var (friendlyTypeName, value, debuggerProxyInstance) = await GetValueForCorDebugValueAsync(implicitThisValue, threadId, stackDepth);
	        var variableInfo = new VariableInfo
	        {
		        Name = "this", // Hardcoded - 'this' has no metadata
		        Value = value,
		        Type = friendlyTypeName,
		        VariablesReference = GetVariablesReference(implicitThisValue, friendlyTypeName, threadId, stackDepth, debuggerProxyInstance)
	        };
	        result.Add(variableInfo);
        }
		var skipCount = isStatic ? 0 : 1; // Skip 'this' for instance methods, as we already handled it
        foreach (var (index, argumentCorDebugValue) in corDebugIlFrame.Arguments.Skip(skipCount).Index())
        {
	        var metadataIndex = isStatic ?  index + 1 : index;
	        // index 0 is the return value, so we add 1 to get to the arguments
	        var paramDef = metadataImport!.GetParamForMethodIndex(corDebugFunction.Token, metadataIndex + 1);
	        var paramProps = metadataImport.GetParamProps(paramDef);
	        var argumentName = paramProps.szName;
	        if (argumentName is null) continue;
	        var (friendlyTypeName, value, debuggerProxyInstance) = await GetValueForCorDebugValueAsync(argumentCorDebugValue, threadId, stackDepth);
	        var variableInfo = new VariableInfo
	        {
		        Name = argumentName,
		        Value = value,
		        Type = friendlyTypeName,
		        VariablesReference = GetVariablesReference(argumentCorDebugValue, friendlyTypeName, threadId, stackDepth, debuggerProxyInstance)
	        };
	        result.Add(variableInfo);
        }
	}

	private int GetVariablesReference(CorDebugValue corDebugValue, string friendlyTypeName, ThreadId threadId, FrameStackDepth stackDepth, CorDebugValue? debuggerProxyInstance)
	{
		var unwrappedDebugValue = corDebugValue.UnwrapDebugValue();
		if (unwrappedDebugValue is CorDebugArrayValue arrayValue)
		{
			if (arrayValue.Count is 0) return 0;
			return GenerateUniqueVariableReference(corDebugValue, threadId, stackDepth, debuggerProxyInstance);
		}
		else if (unwrappedDebugValue is CorDebugObjectValue objectValue)
		{
			var isNullableStruct = friendlyTypeName.EndsWith('?');
			if (isNullableStruct)
			{
				var underlyingValueOrNull = GetUnderlyingValueOrNullFromNullableStruct(objectValue);
				if (underlyingValueOrNull is null) return 0;
				if (underlyingValueOrNull is not CorDebugObjectValue objValue) return 0; // underlying value is primitive
				objectValue = objValue;
			}

			var type = objectValue.Type;
			// Strings are objects but typically displayed as primitives
			if (type is CorElementType.String) return 0;
			if (type is CorElementType.Class or CorElementType.ValueType or CorElementType.SZArray or CorElementType.Array)
			{
				return GenerateUniqueVariableReference(corDebugValue, threadId, stackDepth, debuggerProxyInstance);
			}
		}
		return 0;
	}

	private int GenerateUniqueVariableReference(CorDebugValue value, ThreadId threadId, FrameStackDepth stackDepth, CorDebugValue? debuggerProxyInstance)
	{
		var variablesReference = new VariablesReference(StoredReferenceKind.StackVariable, value, threadId, stackDepth, debuggerProxyInstance);
		var reference = _variableManager.CreateReference(variablesReference);
		return reference;
	}

	private async Task AddMembersAndStaticPseudoVariable(CorDebugValue corDebugValue, CorDebugType corDebugType, VariablesReference variablesReference, List<VariableInfo> result, bool includeNonPublicMembers = true)
	{
		var requiresStaticPseudoVariable = await AddMembers(corDebugValue, corDebugType, variablesReference, result, includeNonPublicMembers);
		if (requiresStaticPseudoVariable)
		{
			var variableInfo = new VariableInfo
			{
				Name = "Static members",
				Value = "",
				Type = "",
				PresentationHint = new VariablePresentationHint { Kind = PresentationHintKind.Class },
				VariablesReference = _variableManager.CreateReference(new VariablesReference(StoredReferenceKind.StaticClassVariable, corDebugValue, variablesReference.ThreadId, variablesReference.FrameStackDepth, null))
			};
			result.Add(variableInfo);
		}
	}

	/// Returns a bool indicating if a Static Members pseudo variable is required
	private async Task<bool> AddMembers(CorDebugValue corDebugValue, CorDebugType corDebugType, VariablesReference variablesReference, List<VariableInfo> result, bool includeNonPublicMembers = true)
    {
	    var hasStaticMembers = false;
	    var corDebugClass = corDebugType.Class;
	    var module = corDebugClass.Module;
	    var mdTypeDef = corDebugClass.Token;
	    var metadataImport = module.GetMetaDataInterface().MetaDataImport;
	    var mdFieldDefs = includeNonPublicMembers ? metadataImport.EnumFields(mdTypeDef) : metadataImport.EnumFields(mdTypeDef).AsValueEnumerable().Where(s => s.IsPublic(metadataImport)).ToArray();
	    var mdProperties = includeNonPublicMembers ? metadataImport.EnumProperties(mdTypeDef) : metadataImport.EnumProperties(mdTypeDef).AsValueEnumerable().Where(s => s.IsPublic(metadataImport)).ToArray();
	    var staticFieldDefs = mdFieldDefs.AsValueEnumerable().Where(s => s.IsStatic(metadataImport)).ToArray();
	    var nonStaticFieldDefs = mdFieldDefs.AsValueEnumerable().Except(staticFieldDefs).ToArray();
	    var staticProperties = mdProperties.AsValueEnumerable().Where(p => p.IsStatic(metadataImport)).ToArray();
	    var nonStaticProperties = mdProperties.AsValueEnumerable().Except(staticProperties).ToArray();
	    if (staticFieldDefs.Length > 0 || staticProperties.Length > 0)
	    {
		    hasStaticMembers = true;
	    }

	    await AddFields(nonStaticFieldDefs, metadataImport, corDebugClass, corDebugValue, result, variablesReference.ThreadId, variablesReference.FrameStackDepth);
	    // We need to pass the un-unwrapped reference value here, as we need to invoke CallParameterizedFunction with the correct parameters
	    await AddProperties(nonStaticProperties, metadataImport, corDebugClass, variablesReference.ThreadId, variablesReference.FrameStackDepth, corDebugValue, result);

	    // Handle members on base types recursively
	    var baseType = corDebugType.Base;
	    if (baseType is null) return hasStaticMembers;
	    var baseTypeName = GetCorDebugTypeFriendlyName(baseType);
	    if (baseTypeName is "System.Object" or "System.ValueType" or "System.Enum") return hasStaticMembers;
		return hasStaticMembers | await AddMembers(corDebugValue, baseType, variablesReference, result);
    }

	private async Task AddStaticMembers(CorDebugValue corDebugValue, CorDebugType corDebugType, VariablesReference variablesReference, List<VariableInfo> result)
    {
	    var corDebugClass = corDebugType.Class;
	    var module = corDebugClass.Module;
	    var mdTypeDef = corDebugClass.Token;
	    var metadataImport = module.GetMetaDataInterface().MetaDataImport;
	    var staticFieldDefs = metadataImport.EnumFields(mdTypeDef).AsValueEnumerable().Where(s => s.IsStatic(metadataImport)).ToArray();
	    var staticProperties = metadataImport.EnumProperties(mdTypeDef).AsValueEnumerable().Where(s => s.IsStatic(metadataImport)).ToArray();

	    await AddFields(staticFieldDefs, metadataImport, corDebugClass, corDebugValue, result, variablesReference.ThreadId, variablesReference.FrameStackDepth);
	    // We need to pass the un-unwrapped reference value here, as we need to invoke CallParameterizedFunction with the correct parameters
	    await AddProperties(staticProperties, metadataImport, corDebugClass, variablesReference.ThreadId, variablesReference.FrameStackDepth, corDebugValue, result);

	    // Handle members on base types recursively
	    var baseType = corDebugType.Base;
	    if (baseType is null) return;
	    var baseTypeName = GetCorDebugTypeFriendlyName(baseType);
	    if (baseTypeName is "System.Object" or "System.ValueType" or "System.Enum") return;
		await AddStaticMembers(corDebugValue, baseType, variablesReference, result);
    }

	private async Task AddFields(mdFieldDef[] mdFieldDefs, MetaDataImport metadataImport, CorDebugClass corDebugClass, CorDebugValue corDebugValue, List<VariableInfo> result, ThreadId threadId, FrameStackDepth stackDepth)
	{
		foreach (var mdFieldDef in mdFieldDefs)
		{
			var fieldProps = metadataImport.GetFieldProps(mdFieldDef);
			var fieldName = fieldProps.szField;
			if (fieldName is null) continue;
			if (Extensions.IsCompilerGeneratedFieldName(fieldName)) continue;
			var isStatic = (fieldProps.pdwAttr & CorFieldAttr.fdStatic) != 0;
			var isLiteral = (fieldProps.pdwAttr & CorFieldAttr.fdLiteral) != 0;
			var debuggerBrowsableRootHidden = false;
			var hasDebuggerBrowsableAttribute = metadataImport.TryGetCustomAttributeByName(mdFieldDef, "System.Diagnostics.DebuggerBrowsableAttribute", out var debuggerBrowsableAttribute) is HRESULT.S_OK;
			if (hasDebuggerBrowsableAttribute)
			{
				// https://github.com/Samsung/netcoredbg/blob/6476bc00c2beaab9255c750235a68de3a3d0cfae/src/debugger/evaluator.cpp#L913
				var debuggerBrowsableState = (DebuggerBrowsableState)GetDebuggerBrowsableCustomAttributeResultInt(debuggerBrowsableAttribute);
				if (debuggerBrowsableState == DebuggerBrowsableState.Never) continue; // I may not end up doing this, as it would be ideal to still be able to hover the variable in the editor and see the value
				if (debuggerBrowsableState == DebuggerBrowsableState.RootHidden) debuggerBrowsableRootHidden = true;
			}
			if (isLiteral)
			{
				var literalValue = GetLiteralValue(fieldProps.ppValue, fieldProps.pdwCPlusTypeFlag);
				var literalVariableInfo = new VariableInfo
				{
					Name = fieldName,
					Value = literalValue.ToString()!,
					Type = GetFriendlyTypeName(fieldProps.pdwCPlusTypeFlag),
					VariablesReference = 0
				};
				result.Add(literalVariableInfo);
				continue;
			}

			var objectValue = corDebugValue.UnwrapDebugValueToObject();
			var fieldCorDebugValue = isStatic ? corDebugClass.GetStaticFieldValue(mdFieldDef, GetFrameForThreadIdAndStackDepth(threadId, stackDepth).Raw) : objectValue.GetFieldValue(corDebugClass.Raw, mdFieldDef);
			if (debuggerBrowsableRootHidden)
			{
				var unwrappedDebugValue = fieldCorDebugValue.UnwrapDebugValue();
		        if (unwrappedDebugValue is CorDebugArrayValue arrayValue)
		        {
			        await AddArrayElements(arrayValue, threadId, stackDepth, result);
					continue;
		        }
			}
			var (friendlyTypeName, value, debuggerProxyInstance) = await GetValueForCorDebugValueAsync(fieldCorDebugValue, threadId, stackDepth);
			var variableInfo = new VariableInfo
			{
				Name = fieldName,
				Value = value,
				Type = friendlyTypeName,
				VariablesReference = GetVariablesReference(fieldCorDebugValue, friendlyTypeName, threadId, stackDepth, debuggerProxyInstance)
			};
			result.Add(variableInfo);
		}
	}

	internal class EvalException(string message) : Exception(message);
	private async Task AddProperties(mdProperty[] mdProperties, MetaDataImport metadataImport, CorDebugClass corDebugClass, ThreadId threadId, FrameStackDepth stackDepth, CorDebugValue corDebugValue, List<VariableInfo> result)
    {
	    foreach (var mdProperty in mdProperties)
	    {
		    var variablesReferenceIlFrame = GetFrameForThreadIdAndStackDepth(threadId, stackDepth);

		    var propertyProps = metadataImport.GetPropertyProps(mdProperty);
		    var propertyName = propertyProps.szProperty;
		    if (propertyName is null) continue;

		    // Get the get method for the property
		    var getMethodDef = propertyProps.pmdGetter;
		    if (getMethodDef == 0) continue; // No get method

		    // Get method attributes to check if it's static
		    var getterMethodProps = metadataImport.GetMethodProps(getMethodDef);
		    var getterAttr = getterMethodProps.pdwAttr;

		    bool isStatic = (getterAttr & CorMethodAttr.mdStatic) != 0;

		    var debuggerBrowsableRootHidden = false;
		    var hasDebuggerBrowsableAttribute = metadataImport.TryGetCustomAttributeByName(mdProperty, "System.Diagnostics.DebuggerBrowsableAttribute", out var debuggerBrowsableAttribute) is HRESULT.S_OK;
		    if (hasDebuggerBrowsableAttribute)
		    {
			    // https://github.com/Samsung/netcoredbg/blob/6476bc00c2beaab9255c750235a68de3a3d0cfae/src/debugger/evaluator.cpp#L913
			    var debuggerBrowsableState = (DebuggerBrowsableState)GetDebuggerBrowsableCustomAttributeResultInt(debuggerBrowsableAttribute);
			    if (debuggerBrowsableState == DebuggerBrowsableState.Never) continue; // I may not end up doing this, as it would be ideal to still be able to hover the variable in the editor and see the value
			    if (debuggerBrowsableState == DebuggerBrowsableState.RootHidden) debuggerBrowsableRootHidden = true;
		    }

		    var getMethod = corDebugClass.Module.GetFunctionFromToken(getMethodDef);
		    var eval = variablesReferenceIlFrame.Chain.Thread.CreateEval();

		    // May not be correct, will need further testing
		    var parameterizedContainingType = corDebugValue.ExactType;

		    var typeParameterTypes = parameterizedContainingType.TypeParameters;
		    var typeParameterArgs = typeParameterTypes.Select(t => t.Raw).ToArray();

		    // For instance properties, pass the object; for static, pass nothing
		    ICorDebugValue[] corDebugValues = isStatic ? [] : [corDebugValue!.Raw];

			var returnValue = await eval.CallParameterizedFunctionAsync(_callbacks, getMethod, typeParameterTypes.Length, typeParameterArgs, corDebugValues.Length, corDebugValues);

		    if (returnValue is null) continue;
		    if (debuggerBrowsableRootHidden)
		    {
			    var unwrappedDebugValue = returnValue.UnwrapDebugValue();
			    if (unwrappedDebugValue is CorDebugArrayValue arrayValue)
			    {
				    await AddArrayElements(arrayValue, threadId, stackDepth, result);
				    continue;
			    }
		    }
		    var (friendlyTypeName, value, debuggerProxyInstance) = await GetValueForCorDebugValueAsync(returnValue, threadId, stackDepth);
		    var variableInfo = new VariableInfo
		    {
			    Name = propertyName,
			    Value = value,
			    Type = friendlyTypeName,
			    VariablesReference = GetVariablesReference(returnValue, friendlyTypeName, threadId, stackDepth, debuggerProxyInstance)
		    };
		    result.Add(variableInfo);
		    if (returnValue is CorDebugHandleValue handleValue)
		    {
			    handleValue.Dispose();
		    }
	    }
    }

	private async Task AddArrayElements(CorDebugArrayValue arrayValue, ThreadId threadId, FrameStackDepth stackDepth, List<VariableInfo> result)
	{
		var rank = arrayValue.Rank;
		if (rank > 1) throw new NotImplementedException("Multidimensional arrays not yet supported");
		var itemCount = arrayValue.Count;

		// Get the elements first, as the CorDebugArrayValue arrayValue may get neutered during 'await GetValueForCorDebugValueAsync' below, if any evals are required
		var elements = ValueEnumerable.Range(0, itemCount).Select(i => arrayValue.GetElement(1, [i])).ToArray();
		foreach (var (i, element) in elements.Index())
		{
			var (friendlyTypeName, value, debuggerProxyInstance) = await GetValueForCorDebugValueAsync(element, threadId, stackDepth);
			var variableReference = GetVariablesReference(element, friendlyTypeName, threadId, stackDepth, debuggerProxyInstance);
			var variableInfo = new VariableInfo
			{
				Name = $"[{i}]",
				Type = friendlyTypeName,
				Value = value,
				VariablesReference = variableReference
			};
			result.Add(variableInfo);
		}
	}
}
