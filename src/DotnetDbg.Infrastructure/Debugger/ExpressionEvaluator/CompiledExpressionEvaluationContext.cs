using ClrDebug;

namespace DotnetDbg.Infrastructure.Debugger.ExpressionEvaluator;

public class CompiledExpressionEvaluationContext(CorDebugThread thread, ThreadId threadId, FrameStackDepth stackDepth)
{
	public CorDebugThread Thread { get; set; } = thread;
	public FrameStackDepth StackDepth { get; set; } = stackDepth;
	public ThreadId ThreadId { get; set; } = threadId;
}

public class RuntimeAssemblyPrimitiveTypeClasses(Dictionary<CorElementType, CorDebugClass> corElementToValueClassMap, CorDebugClass? corVoidClass, CorDebugClass? corDecimalClass)
{
	public Dictionary<CorElementType, CorDebugClass> CorElementToValueClassMap { get; } = corElementToValueClassMap;
	public CorDebugClass? CorVoidClass { get; } = corVoidClass;
	public CorDebugClass? CorDecimalClass { get; } = corDecimalClass;
}
