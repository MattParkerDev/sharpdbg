using ClrDebug;
using Microsoft.CodeAnalysis.CSharp.Symbols;

namespace SharpDbg.Infrastructure.Debugger;

public partial class ManagedDebugger
{
	private static CorDebugValue? GetAsyncOrLambdaProxyFieldValue(CorDebugValue compilerGeneratedClassValue, MetaDataImport metadataImport)
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
				var fieldCorDebugValue = objectValue.GetFieldValue(objectValue.Class.Raw, field);
				return fieldCorDebugValue;
			}
			else if (generatedNameKind is GeneratedNameKind.DisplayClassLocalOrField)
			{
				throw new NotImplementedException();
			}
		}

		// E.g. in a static async method, there is no 'this' proxy field.
		return null;
	}
}
