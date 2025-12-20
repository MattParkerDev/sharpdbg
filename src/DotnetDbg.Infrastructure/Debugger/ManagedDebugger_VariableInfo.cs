using System.Runtime.InteropServices;
using ClrDebug;

namespace DotnetDbg.Infrastructure.Debugger;

public partial class ManagedDebugger
{
	private void AddLocalVariables(CorDebugILFrame corDebugIlFrame, ModuleInfo module, CorDebugFunction corDebugFunction, List<VariableInfo> result)
	{
		if (corDebugIlFrame.LocalVariables.Length is 0) return;
		foreach (var (index, localVariableCorDebugValue) in corDebugIlFrame.LocalVariables.Index())
		{
			var localVariableName = module.SymbolReader?.GetLocalVariableName(corDebugFunction.Token, index);
			if (localVariableName is null) continue; // Compiler generated locals will not be found. E.g. DefaultInterpolatedStringHandler
			var (friendlyTypeName, value) = GetValueForCorDebugValue(localVariableCorDebugValue);
			var variableInfo = new VariableInfo
			{
				Name = localVariableName,
				Value = value,
				Type = friendlyTypeName,
				VariablesReference = GetVariablesReference(localVariableCorDebugValue, corDebugIlFrame)
			};
			result.Add(variableInfo);
		}
	}

	private void AddArguments(CorDebugILFrame corDebugIlFrame, ModuleInfo module, CorDebugFunction corDebugFunction, List<VariableInfo> result)
	{
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
	        var (friendlyTypeName, value) = GetValueForCorDebugValue(implicitThisValue);
	        var variableInfo = new VariableInfo
	        {
		        Name = "this", // Hardcoded - 'this' has no metadata
		        Value = value,
		        Type = friendlyTypeName,
		        VariablesReference = GetVariablesReference(implicitThisValue, corDebugIlFrame)
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
	        var (friendlyTypeName, value) = GetValueForCorDebugValue(argumentCorDebugValue);
	        var variableInfo = new VariableInfo
	        {
		        Name = argumentName,
		        Value = value,
		        Type = friendlyTypeName,
		        VariablesReference = GetVariablesReference(argumentCorDebugValue, corDebugIlFrame)
	        };
	        result.Add(variableInfo);
        }
	}

	private int GetVariablesReference(CorDebugValue corDebugValue, CorDebugILFrame corDebugIlFrame)
	{
		try
		{
			// Dereference if it's a reference type
			var valueToCheck = corDebugValue;
			if (corDebugValue is CorDebugReferenceValue { IsNull: false } refValue)
			{
				valueToCheck = refValue.Dereference();
			}

			if (valueToCheck is CorDebugObjectValue objectValue)
			{
				var type = objectValue.Type;
				// Strings are objects but typically displayed as primitives
				if (type is CorElementType.String) return 0;
				if (type is CorElementType.Class or CorElementType.ValueType or CorElementType.SZArray or CorElementType.Array)
				{
					return GenerateUniqueVariableReference(corDebugValue, corDebugIlFrame);
				}
			}
		}
		catch
		{
			// If anything fails, assume no variables
			return 0;
		}

		return 0;
	}

	private int GenerateUniqueVariableReference(CorDebugValue value, CorDebugILFrame corDebugIlFrame)
	{
		var stackDepth = corDebugIlFrame.Chain.Frames.IndexOf(corDebugIlFrame);
		var threadId = corDebugIlFrame.Chain.Thread.Id;
		var variablesReference = new VariablesReference(StoredReferenceKind.StackVariable, value, new ThreadId(threadId), new FrameStackDepth(stackDepth));
		var reference = _variableManager.CreateReference(variablesReference);
		return reference;
	}

	private void AddFields(mdFieldDef[] mdFieldDefs, MetaDataImport metadataImport, CorDebugClass corDebugClass, CorDebugILFrame ilFrame, CorDebugObjectValue objectValue, List<VariableInfo> result)
	{
		foreach (var mdFieldDef in mdFieldDefs)
		{
			var fieldProps = metadataImport.GetFieldProps(mdFieldDef);
			var fieldName = fieldProps.szField;
			if (fieldName is null) continue;
			if (Extensions.IsCompilerGeneratedFieldName(fieldName)) continue;
			var isStatic = (fieldProps.pdwAttr & CorFieldAttr.fdStatic) != 0;
			var fieldCorDebugValue = isStatic ? corDebugClass.GetStaticFieldValue(mdFieldDef, ilFrame.Raw) : objectValue.GetFieldValue(corDebugClass.Raw, mdFieldDef);
			var (friendlyTypeName, value) = GetValueForCorDebugValue(fieldCorDebugValue);
			var variableInfo = new VariableInfo
			{
				Name = fieldName,
				Value = value,
				Type = friendlyTypeName,
				VariablesReference = GetVariablesReference(fieldCorDebugValue, ilFrame)
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

			// Ensure that the object passed in corDebugValues is a CorDebugReferenceValue (when containing object is an instance class), ie must not be dereferenced
		    eval.CallParameterizedFunction(getMethod.Raw, typeParameterArgs.Length, typeParameterArgs, corDebugValues.Length, corDebugValues);

		    // Wait for the eval to complete
		    CorDebugValue? returnValue = null;
		    var evalCompleteTcs = new TaskCompletionSource();

		    void OnCallbacksOnOnEvalComplete(object? s, EvalCompleteCorDebugManagedCallbackEventArgs e)
		    {
			    if (e.Eval.Raw == eval.Raw)
			    {
				    returnValue = e.Eval.Result;
				    evalCompleteTcs.SetResult();
			    }
		    }

		    _callbacks.OnEvalComplete += OnCallbacksOnOnEvalComplete;
		    _callbacks.OnEvalException += CallbacksOnOnEvalException;

		    void CallbacksOnOnEvalException(object? sender, EvalExceptionCorDebugManagedCallbackEventArgs e)
		    {
			    if (e.Eval.Raw == eval.Raw)
			    {
				    if (e.Eval.Result is null)
				    {
					    var exception = new EvalException($"EvalException callback error - Result is null when evaluating property '{propertyName}' (Static: {isStatic})");
					    _logger?.Invoke(exception.Message);
					    evalCompleteTcs.SetException(exception);
					    return;
				    }
				    returnValue = e.Eval.Result;
				    evalCompleteTcs.SetResult();
			    }
		    }

		    variablesReferenceIlFrame.Chain.Thread.Process.Continue(false);

		    await evalCompleteTcs.Task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
		    _callbacks.OnEvalComplete -= OnCallbacksOnOnEvalComplete;
		    _callbacks.OnEvalException -= CallbacksOnOnEvalException;
		    await evalCompleteTcs.Task;

		    if (returnValue is null) continue;
		    var (friendlyTypeName, value) = GetValueForCorDebugValue(returnValue);
		    // eval neutered the frame again, and we need it to get variables for nested objects (specifically static fields/properties)
		    variablesReferenceIlFrame = GetFrameForThreadIdAndStackDepth(threadId, stackDepth);
		    var variableInfo = new VariableInfo
		    {
			    Name = propertyName,
			    Value = value,
			    Type = friendlyTypeName,
			    VariablesReference = GetVariablesReference(returnValue, variablesReferenceIlFrame)
		    };
		    result.Add(variableInfo);
		    if (returnValue is CorDebugHandleValue handleValue)
		    {
			    handleValue.Dispose();
		    }
	    }
    }
}
