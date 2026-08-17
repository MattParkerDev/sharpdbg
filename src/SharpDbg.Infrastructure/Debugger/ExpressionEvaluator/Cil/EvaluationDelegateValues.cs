using System.Reflection.Metadata;
using ICorDebugSharp;

namespace SharpDbg.Infrastructure.Debugger.ExpressionEvaluator.Cil;

internal sealed class EvaluationObject(TypeDefinitionHandle type, MethodDefinitionHandle constructor)
{
	public TypeDefinitionHandle Type { get; } = type;
	public MethodDefinitionHandle Constructor { get; } = constructor;
	public Dictionary<FieldDefinitionHandle, ICilLocation> Fields { get; } = new();
	public Dictionary<FieldDefinitionHandle, ICilLocation> FieldBindings { get; } = new();
	public ICorDebugValue? MaterializedValue { get; set; }
}

internal sealed record EvaluationFunctionPointer(int MethodToken, ResolvedRuntimeMethod? RuntimeMethod = null);
internal sealed record EvaluationDelegate(EvaluationFunctionPointer Function, CilValue Target, ResolvedRuntimeType DelegateType);
