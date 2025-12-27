using ClrDebug;

namespace DotnetDbg.Infrastructure.Debugger.ExpressionEvaluator;

public class CompiledExpressionEvaluationContext
{
	public required ManagedDebugger Debugger { get; set; }
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
	public int FrameLevel { get; set; }
	public CorDebugILFrame ILFrame { get; set; }

	public EvalData(CorDebugThread thread, int frameLevel, CorDebugILFrame ilFrame)
	{
		Thread = thread;
		FrameLevel = frameLevel;
		ILFrame = ilFrame;
	}
}
