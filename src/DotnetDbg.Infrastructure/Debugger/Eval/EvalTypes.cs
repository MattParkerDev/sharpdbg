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

public class EvalStackEntry
{
	public CorDebugValue? CorDebugValue { get; set; }
	public List<string> Identifiers { get; set; } = new();
	public List<CorDebugType?>? GenericTypeCache { get; set; }
	public bool Literal { get; set; }
	public bool Editable { get; set; }
	public bool PreventBinding { get; set; }
	public SetterData? SetterData { get; set; }

	public void ResetEntry()
	{
		CorDebugValue = null;
		Identifiers.Clear();
		GenericTypeCache = null;
		Literal = false;
	}

	public void ResetEntry(ResetLiteralStatus resetLiteral)
	{
		CorDebugValue = null;
		Identifiers.Clear();
		GenericTypeCache = null;
		if (resetLiteral == ResetLiteralStatus.Yes)
		{
			Literal = false;
		}
	}

	public enum ResetLiteralStatus
	{
		Yes,
		No
	}
}

public class SetterData
{
	public CorDebugValue? OwnerValue { get; set; }
	public CorDebugFunction? SetterFunction { get; set; }
}

public class EvalData
{
	public CorDebugThread Thread { get; set; } = null!;
	public int FrameLevel { get; set; }
	public int EvalFlags { get; set; }
	public Dictionary<CorElementType, CorDebugClass> CorElementToValueClassMap { get; set; } = new();
	public CorDebugClass? ICorVoidClass { get; set; }
	public CorDebugClass? ICorDecimalClass { get; set; }

	public EvalData(CorDebugThread thread, int frameLevel, int evalFlags)
	{
		Thread = thread;
		FrameLevel = frameLevel;
		EvalFlags = evalFlags;
	}
}
