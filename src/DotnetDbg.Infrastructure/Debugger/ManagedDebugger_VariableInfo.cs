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
			var value = GetValueForCorDebugValue(localVariableCorDebugValue);
			var typeName = GetFriendlyTypeName(localVariableCorDebugValue.Type);
			var variableInfo = new VariableInfo
			{
				Name = localVariableName,
				Value = value,
				Type = typeName,
				VariablesReference = 0 // TODO: set if complex type
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
	        var variableInfo = new VariableInfo
	        {
		        Name = "this", // Hardcoded - 'this' has no metadata
		        Value = GetValueForCorDebugValue(implicitThisValue),
		        Type = implicitThisValue.ExactType.Type.ToString(),
		        VariablesReference = 0 // TODO: set if complex type
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
	        var value = GetValueForCorDebugValue(argumentCorDebugValue);
	        var typeName = GetFriendlyTypeName(argumentCorDebugValue.Type);
	        var variableInfo = new VariableInfo
	        {
		        Name = argumentName,
		        Value = value,
		        Type = typeName,
		        VariablesReference = 0 // TODO: set if complex type
	        };
	        result.Add(variableInfo);
        }
	}

	private string GetFriendlyTypeName(CorElementType elementType)
	{
		return elementType switch
		{
			CorElementType.Void => "void",
			CorElementType.Boolean => "bool",
			CorElementType.Char => "char",
			CorElementType. I1 => "sbyte",
			CorElementType.U1 => "byte",
			CorElementType.I2 => "short",
			CorElementType.U2 => "ushort",
			CorElementType.I4 => "int",
			CorElementType. U4 => "uint",
			CorElementType.I8 => "long",
			CorElementType.U8 => "ulong",
			CorElementType.R4 => "float",
			CorElementType.R8 => "double",
			CorElementType.String => "string",
			CorElementType.Object => "object",
			CorElementType.I => "nint",
			CorElementType.U => "nuint",
			_ => throw new ArgumentOutOfRangeException(),
		};
	}
}
