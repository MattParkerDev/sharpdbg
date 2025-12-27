using ClrDebug;

namespace DotnetDbg.Infrastructure.Debugger.ExpressionEvaluator;

public class CompiledExpressionEvaluationContext
{
	public required ManagedDebugger Debugger { get; set; }
	public required EvalData EvalData { get; set; }
}

public class EvalData
{
	public CorDebugThread Thread { get; set; }
	public int FrameLevel { get; set; }
	//public int EvalFlags { get; set; }
	public Dictionary<CorElementType, CorDebugClass> CorElementToValueClassMap { get; set; }
	public CorDebugClass? ICorVoidClass { get; set; }
	public CorDebugClass? ICorDecimalClass { get; set; }
	public CorDebugManagedCallback ManagedCallback { get; set; }
	public CorDebugILFrame ILFrame { get; set; }

	public EvalData(CorDebugThread thread, int frameLevel, CorDebugManagedCallback managedCallback, CorDebugILFrame ilFrame, Dictionary<CorElementType, CorDebugClass> corElementToValueClassMap, CorDebugClass? corVoidClass, CorDebugClass? corDecimalClass)
	{
		Thread = thread;
		FrameLevel = frameLevel;
		//EvalFlags = evalFlags;
		ManagedCallback = managedCallback;
		ILFrame = ilFrame;
		CorElementToValueClassMap = corElementToValueClassMap;
		ICorVoidClass = corVoidClass;
		ICorDecimalClass = corDecimalClass;
	}
}
