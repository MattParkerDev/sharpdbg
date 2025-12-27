using ClrDebug;

namespace DotnetDbg.Infrastructure.Debugger.ExpressionEvaluator;

public class CompiledExpressionEvaluationContext
{
	public required EvalData EvalData { get; set; }
}

public class RuntimeAssemblyPrimitiveTypeClasses(Dictionary<CorElementType, CorDebugClass> corElementToValueClassMap, CorDebugClass? corVoidClass, CorDebugClass? corDecimalClass)
{
	public Dictionary<CorElementType, CorDebugClass> CorElementToValueClassMap { get; } = corElementToValueClassMap;
	public CorDebugClass? CorVoidClass { get; } = corVoidClass;
	public CorDebugClass? CorDecimalClass { get; } = corDecimalClass;
}

public class EvalData
{
	public CorDebugThread Thread { get; set; }
	public FrameStackDepth StackDepth { get; set; }
	public ThreadId ThreadId { get; set; }

	public EvalData(CorDebugThread thread, ThreadId threadId, FrameStackDepth stackDepth)
	{
		Thread = thread;
		ThreadId = threadId;
		StackDepth = stackDepth;
	}
}
