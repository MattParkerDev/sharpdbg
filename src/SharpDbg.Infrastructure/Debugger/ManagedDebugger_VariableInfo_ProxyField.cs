using ICorDebugSharp;
using Microsoft.CodeAnalysis.CSharp.Symbols;

namespace SharpDbg.Infrastructure.Debugger;

public partial class ManagedDebugger
{
	private static ICorDebugValue? GetAsyncOrLambdaProxyFieldValue(ICorDebugValue compilerGeneratedClassValue, IMetaDataImport metadataImport)
	{
		var objectValue = compilerGeneratedClassValue.UnwrapDebugValueToObject();
		var fields = metadataImport.EnumFields(objectValue.Class.Token);
		foreach (var field in fields)
		{
			var fieldProps = metadataImport.GetFieldProps(field);
			var fieldName = fieldProps.szField;
			var generatedNameKind = GeneratedNameParser.GetKind(fieldName);
			if (generatedNameKind is GeneratedNameKind.ThisProxyField)
			{
				var fieldCorDebugValue = objectValue.GetFieldValue(objectValue.Class, field);
				return fieldCorDebugValue;
			}
			else if (generatedNameKind is GeneratedNameKind.DisplayClassLocalOrField)
			{
				// This field points to a parent closure class - follow the chain to find 'this'
				var parentClosureValue = objectValue.GetFieldValue(objectValue.Class, field);
				var parentObjectValue = parentClosureValue.UnwrapDebugValueToObject();
				var parentMetadataImport = parentObjectValue.Class.Module.GetMetaDataInterface<IMetaDataImport>();
				return GetAsyncOrLambdaProxyFieldValue(parentClosureValue, parentMetadataImport);
			}
		}

		// E.g. in a static async method, there is no 'this' proxy field.
		return null;
	}
}
