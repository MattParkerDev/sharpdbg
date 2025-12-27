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
