using ClrDebug;

namespace SharpDbg.Infrastructure.Debugger.ExpressionEvaluator;

public class CompiledExpressionEvaluationContext(CorDebugThread thread, ThreadId threadId, FrameStackDepth stackDepth, CorDebugValue? rootValue = null)
{
	public CorDebugThread Thread { get; set; } = thread;
	public FrameStackDepth StackDepth { get; set; } = stackDepth;
	public ThreadId ThreadId { get; set; } = threadId;
	/// Used as the root value for identifier resolution, if provided. Primarily for evaluating DebuggerDisplay expressions, which only have access to the current object.
	public CorDebugValue? RootValue { get; set; } = rootValue;
}

public class RuntimeAssemblyPrimitiveTypeClasses(Dictionary<CorElementType, CorDebugClass> corElementToValueClassMap, CorDebugClass? corVoidClass, CorDebugClass? corDecimalClass)
{
	public Dictionary<CorElementType, CorDebugClass> CorElementToValueClassMap { get; } = corElementToValueClassMap;
	public CorDebugClass? CorVoidClass { get; } = corVoidClass;
	public CorDebugClass? CorDecimalClass { get; } = corDecimalClass;
}
