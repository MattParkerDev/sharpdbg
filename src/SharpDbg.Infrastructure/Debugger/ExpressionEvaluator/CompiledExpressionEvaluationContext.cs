using ICorDebugSharp;

namespace SharpDbg.Infrastructure.Debugger.ExpressionEvaluator;

public class CompiledExpressionEvaluationContext(ICorDebugThread thread, ThreadId threadId, FrameStackDepth stackDepth, ICorDebugValue? rootValue = null)
{
	public ICorDebugThread Thread { get; set; } = thread;
	public FrameStackDepth StackDepth { get; set; } = stackDepth;
	public ThreadId ThreadId { get; set; } = threadId;
	/// Used as the root value for identifier resolution, if provided. Primarily for evaluating DebuggerDisplay expressions, which only have access to the current object.
	public ICorDebugValue? RootValue { get; set; } = rootValue;
}

public class RuntimeAssemblyPrimitiveTypeClasses(Dictionary<CorElementType, ICorDebugClass> corElementToValueClassMap, ICorDebugClass? corVoidClass, ICorDebugClass? corDecimalClass)
{
	public Dictionary<CorElementType, ICorDebugClass> CorElementToValueClassMap { get; } = corElementToValueClassMap;
	public ICorDebugClass? CorVoidClass { get; } = corVoidClass;
	public ICorDebugClass? CorDecimalClass { get; } = corDecimalClass;
}
