using System.Runtime.CompilerServices;
using ClrDebug;

namespace DotnetDbg.Infrastructure.Debugger;

public static class Extensions
{
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool IsStatic(this mdFieldDef mdFieldDef, MetaDataImport metadataImport)
	{
		var fieldProps = metadataImport.GetFieldProps(mdFieldDef);
		var isStatic = (fieldProps.pdwAttr & CorFieldAttr.fdStatic) != 0;
		return isStatic;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool IsStatic(this mdProperty mdProperty, MetaDataImport metadataImport)
	{
		var propertyProps = metadataImport.GetPropertyProps(mdProperty);
		var getterMethodProps = metadataImport.GetMethodProps(propertyProps.pmdGetter);
		var getterAttr = getterMethodProps.pdwAttr;

		var isStatic = (getterAttr & CorMethodAttr.mdStatic) != 0;
		return isStatic;
	}
}
