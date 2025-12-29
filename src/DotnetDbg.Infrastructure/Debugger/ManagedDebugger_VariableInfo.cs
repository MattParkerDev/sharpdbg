using System.Runtime.InteropServices;
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

	private async Task AddMembersForDebugProxyType(CorDebugValue corDebugValue, CorDebugObjectValue dereferencedObjectValue, VariablesReference variablesReference, List<VariableInfo> result)
    {
	    var corDebugClass = dereferencedObjectValue.Class;
	    var module = corDebugClass.Module;
	    var mdTypeDef = corDebugClass.Token;
	    var metadataImport = module.GetMetaDataInterface().MetaDataImport;
	    var mdFieldDefs = metadataImport.EnumFields(mdTypeDef).AsValueEnumerable().Where(s => s.IsPublic(metadataImport)).ToArray();
	    var mdProperties = metadataImport.EnumProperties(mdTypeDef).AsValueEnumerable().Where(s => s.IsPublic(metadataImport)).ToArray();
	    var staticFieldDefs = mdFieldDefs.AsValueEnumerable().Where(s => s.IsStatic(metadataImport)).ToArray();
	    var nonStaticFieldDefs = mdFieldDefs.AsValueEnumerable().Except(staticFieldDefs).ToArray();
	    var staticProperties = mdProperties.AsValueEnumerable().Where(p => p.IsStatic(metadataImport)).ToArray();
	    var nonStaticProperties = mdProperties.AsValueEnumerable().Except(staticProperties).ToArray();
	    if (staticFieldDefs.Length > 0 || staticProperties.Length > 0)
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
	    //AddStaticMembersPseudoVariable(staticFieldDefs, staticProperties, metadataImport, corDebugClass, variablesReference.IlFrame, result);
	    await AddFields(nonStaticFieldDefs, metadataImport, corDebugClass, corDebugValue, result, variablesReference.ThreadId, variablesReference.FrameStackDepth);
	    // We need to pass the un-unwrapped reference value here, as we need to invoke CallParameterizedFunction with the correct parameters
	    await AddProperties(nonStaticProperties, metadataImport, corDebugClass, variablesReference.ThreadId, variablesReference.FrameStackDepth, corDebugValue, result);
    }

	private async Task AddMembers(CorDebugValue corDebugValue, CorDebugObjectValue dereferencedObjectValue, VariablesReference variablesReference, List<VariableInfo> result)
    {
	    var corDebugClass = dereferencedObjectValue.Class;
	    var module = corDebugClass.Module;
	    var mdTypeDef = corDebugClass.Token;
	    var metadataImport = module.GetMetaDataInterface().MetaDataImport;
	    var mdFieldDefs = metadataImport.EnumFields(mdTypeDef);
	    var mdProperties = metadataImport.EnumProperties(mdTypeDef);
	    var staticFieldDefs = mdFieldDefs.AsValueEnumerable().Where(s => s.IsStatic(metadataImport)).ToArray();
	    var nonStaticFieldDefs = mdFieldDefs.AsValueEnumerable().Except(staticFieldDefs).ToArray();
	    var staticProperties = mdProperties.AsValueEnumerable().Where(p => p.IsStatic(metadataImport)).ToArray();
	    var nonStaticProperties = mdProperties.AsValueEnumerable().Except(staticProperties).ToArray();
	    if (staticFieldDefs.Length > 0 || staticProperties.Length > 0)
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
	    //AddStaticMembersPseudoVariable(staticFieldDefs, staticProperties, metadataImport, corDebugClass, variablesReference.IlFrame, result);
	    await AddFields(nonStaticFieldDefs, metadataImport, corDebugClass, corDebugValue, result, variablesReference.ThreadId, variablesReference.FrameStackDepth);
	    // We need to pass the un-unwrapped reference value here, as we need to invoke CallParameterizedFunction with the correct parameters
	    await AddProperties(nonStaticProperties, metadataImport, corDebugClass, variablesReference.ThreadId, variablesReference.FrameStackDepth, corDebugValue, result);
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
			var hasDebuggerBrowsableAttribute = metadataImport.TryGetCustomAttributeByName(mdFieldDef, "System.Diagnostics.DebuggerBrowsableAttribute", out var debuggerBrowsableAttribute) is HRESULT.S_OK;
			if (hasDebuggerBrowsableAttribute)
			{
				;
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

		    var getMethod = corDebugClass.Module.GetFunctionFromToken(getMethodDef);
		    var eval = variablesReferenceIlFrame.Chain.Thread.CreateEval();

		    // May not be correct, will need further testing
		    var parameterizedContainingType = corDebugClass.GetParameterizedType(
			    isStatic ? CorElementType.Class : (corDebugValue?.Type ?? CorElementType.Class),
			    0,
			    []);

		    var typeParameterTypes = parameterizedContainingType.TypeParameters;
		    var typeParameterArgs = typeParameterTypes.Select(t => t.Raw).ToArray();

		    // For instance properties, pass the object; for static, pass nothing
		    ICorDebugValue[] corDebugValues = isStatic ? [] : [corDebugValue!.Raw];

			var returnValue = await eval.CallParameterizedFunctionAsync(_callbacks, getMethod, typeParameterTypes.Length, typeParameterArgs, corDebugValues.Length, corDebugValues);

		    if (returnValue is null) continue;
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
}
