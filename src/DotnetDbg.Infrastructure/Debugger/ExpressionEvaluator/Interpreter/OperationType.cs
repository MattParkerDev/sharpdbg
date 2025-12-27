namespace DotnetDbg.Infrastructure.Debugger.ExpressionEvaluator.Interpreter;

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
