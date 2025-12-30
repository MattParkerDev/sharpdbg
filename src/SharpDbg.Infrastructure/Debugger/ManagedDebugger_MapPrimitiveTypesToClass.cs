using System.Runtime.InteropServices;
using ClrDebug;

namespace SharpDbg.Infrastructure.Debugger;

public partial class ManagedDebugger
{
	public Dictionary<CorElementType, CorDebugClass> CorElementToValueClassMap { get; set; } = [];
	public CorDebugClass? CorDecimalClass { get; set; }
	public CorDebugClass? CorVoidClass { get; set; }

	private void MapRuntimePrimitiveTypesToCorDebugClass(CorDebugModule module)
	{
		if (Path.GetFileName(module.Name) is not "System.Private.CoreLib.dll") throw new InvalidOperationException("Mapping primitive types to classes is only supported for System.Private.CoreLib.dll");
		var metadataImport = module.GetMetaDataInterface().MetaDataImport;

		var typeDef = metadataImport.FindTypeDefByNameOrNull("System.Decimal", mdToken.Nil);
		if (typeDef is null || typeDef.Value.IsNil) throw new InvalidOperationException("Could not find System.Decimal type definition");
		CorDecimalClass = module.GetClassFromToken(typeDef.Value);

		typeDef = metadataImport.FindTypeDefByNameOrNull("System.Void", mdToken.Nil);
		if (typeDef is null || typeDef.Value.IsNil) throw new InvalidOperationException("Could not find System.Void type definition");
		CorVoidClass = module.GetClassFromToken(typeDef.Value);

		var corElementToValueNameMap = new[]
		{
			(CorElementType.Boolean, "System.Boolean"),
			(CorElementType.Char, "System.Char"),
			(CorElementType.I1, "System.SByte"),
			(CorElementType.U1, "System.Byte"),
			(CorElementType.I2, "System.Int16"),
			(CorElementType.U2, "System.UInt16"),
			(CorElementType.I4, "System.Int32"),
			(CorElementType.U4, "System.UInt32"),
			(CorElementType.I8, "System.Int64"),
			(CorElementType.U8, "System.UInt64"),
			(CorElementType.R4, "System.Single"),
			(CorElementType.R8, "System.Double")
		};

		foreach (var (corElementType, typeName) in corElementToValueNameMap)
		{
			var typedef = metadataImport.FindTypeDefByNameOrNull(typeName, mdToken.Nil);
			if (typedef is null || typedef.Value.IsNil) throw new InvalidOperationException($"Could not find {typeName} type definition");
			CorElementToValueClassMap[corElementType] = module.GetClassFromToken(typedef.Value);
		}
	}
}
