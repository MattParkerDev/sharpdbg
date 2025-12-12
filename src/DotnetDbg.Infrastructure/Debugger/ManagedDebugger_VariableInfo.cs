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
				VariablesReference = GetVariablesReference(localVariableCorDebugValue)
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
		        VariablesReference = GetVariablesReference(implicitThisValue)
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
		        VariablesReference = GetVariablesReference(argumentCorDebugValue)
	        };
	        result.Add(variableInfo);
        }
	}

	private int GetVariablesReference(CorDebugValue corDebugValue)
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
					return GenerateUniqueVariableReference(objectValue);
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

	private int GenerateUniqueVariableReference(CorDebugObjectValue value)
	{
		var reference = _variableManager.CreateReference(value);
		return reference;
	}
}
