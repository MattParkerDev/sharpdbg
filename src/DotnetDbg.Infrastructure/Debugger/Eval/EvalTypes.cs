using ClrDebug;

namespace DotnetDbg.Infrastructure.Debugger.Eval;

public enum BasicTypes
{
	TypeBoolean = 1,
	TypeByte,
	TypeSByte,
	TypeChar,
	TypeDouble,
	TypeSingle,
	TypeInt32,
	TypeUInt32,
	TypeInt64,
	TypeUInt64,
	TypeInt16,
	TypeUInt16,
	TypeString
}

public enum OperationType
{
	AddExpression = 1,
	SubtractExpression,
	MultiplyExpression,
	DivideExpression,
	ModuloExpression,
	RightShiftExpression,
	LeftShiftExpression,
	BitwiseNotExpression,
	LogicalAndExpression,
	LogicalOrExpression,
	ExclusiveOrExpression,
	BitwiseAndExpression,
	BitwiseOrExpression,
	LogicalNotExpression,
	EqualsExpression,
	NotEqualsExpression,
	LessThanExpression,
	GreaterThanExpression,
	LessThanOrEqualExpression,
	GreaterThanOrEqualExpression,
	UnaryPlusExpression,
	UnaryMinusExpression
}

public enum CheckedState
{
	Unchecked,
	Checked
}

public class SetterData
{
	public CorDebugValue? OwnerValue { get; set; }
	public CorDebugFunction? SetterFunction { get; set; }
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
