using ClrDebug;

namespace SharpDbg.Infrastructure.Debugger;

public partial class ManagedDebugger
{
	private static string GetEnumDisplayValue(MetaDataImport metaDataImport, CorDebugObjectValue corDebugObjectValue, string valueAsString)
	{
		var hasFlagsAttribute = metaDataImport.TryGetCustomAttributeByName(corDebugObjectValue.Class.Token, "System.FlagsAttribute", out _) is HRESULT.S_OK;

		// Fast path: exact match
		var exact = GetEnumNameForValue(metaDataImport, corDebugObjectValue, valueAsString);
		if (exact is not null) return exact;

		if (hasFlagsAttribute is false) return valueAsString;

		return GetFlagsEnumValue(metaDataImport, corDebugObjectValue, valueAsString);
	}

	private static string? GetEnumNameForValue(MetaDataImport metaDataImport, CorDebugObjectValue corDebugObjectValue, string valueAsString)
	{
		var fields = metaDataImport.EnumFields(corDebugObjectValue.Class.Token);
		foreach (var field in fields)
		{
			const CorFieldAttr requiredAttributesForEnumOption = CorFieldAttr.fdPublic | CorFieldAttr.fdStatic | CorFieldAttr.fdLiteral | CorFieldAttr.fdHasDefault;
			var fieldProps = metaDataImport.GetFieldProps(field);
			if ((fieldProps.pdwAttr & requiredAttributesForEnumOption) != requiredAttributesForEnumOption) continue;
			var fieldValue = GetLiteralValue(fieldProps.ppValue, fieldProps.pdwCPlusTypeFlag);
			if (fieldValue.ToString() == valueAsString)
			{
				return fieldProps.szField;
			}
		}
		return null;
	}

	private static string GetFlagsEnumValue(MetaDataImport metaDataImport, CorDebugObjectValue corDebugObjectValue, string valueAsString)
	{
		if (!ulong.TryParse(valueAsString, out var enumValue))
			return valueAsString;

		ulong remaining = enumValue;

		// value -> name, ordered by value (ascending)
		var flags = new SortedDictionary<ulong, string>();

		foreach (var field in metaDataImport.EnumFields(corDebugObjectValue.Class.Token))
		{
			const CorFieldAttr requiredAttributesForEnumOption = CorFieldAttr.fdPublic | CorFieldAttr.fdStatic | CorFieldAttr.fdLiteral | CorFieldAttr.fdHasDefault;

			var fieldProps = metaDataImport.GetFieldProps(field);
			if ((fieldProps.pdwAttr & requiredAttributesForEnumOption) != requiredAttributesForEnumOption) continue;

			var fieldValueObj = GetLiteralValue(fieldProps.ppValue, fieldProps.pdwCPlusTypeFlag);

			ulong fieldValue = Convert.ToUInt64(fieldValueObj);

			// Zero flag is excluded from OR expressions
			if (fieldValue is 0) continue;

			// Exact match already handled earlier
			if ((fieldValue & remaining) == fieldValue)
			{
				flags[fieldValue] = fieldProps.szField;
				remaining &= ~fieldValue;
			}
		}

		// Only return flags if we fully decomposed the value
		if (flags.Count > 0 && remaining == 0)
		{
			return string.Join(" | ", flags.Values);
		}

		// Fallback: numeric value
		return enumValue.ToString();
	}
}
