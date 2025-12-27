using System.Runtime.InteropServices;
using ClrDebug;

namespace DotnetDbg.Infrastructure.Debugger;

public partial class ManagedDebugger
{
	public Dictionary<CorElementType, CorDebugClass> CorElementToValueClassMap { get; set; } = [];

	private void MapRuntimePrimitiveTypesToCorDebugClass(CorDebugModule module)
	{
		if (module.Name is not "System.Private.CoreLib.dll") throw new InvalidOperationException("Mapping primitive types to classes is only supported for System.Private.CoreLib.dll");
		var metadataImport = module.GetMetaDataInterface().MetaDataImport;

	}
}
