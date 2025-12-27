using System.Text;
using Microsoft.CodeAnalysis.CSharp;
using static DotnetDbg.Infrastructure.Debugger.ExpressionEvaluator.Compiler.CompilerConstants;

namespace DotnetDbg.Infrastructure.Debugger.Eval;

enum ePredefinedType
{
	BoolKeyword,
	ByteKeyword,
	CharKeyword,
	DecimalKeyword,
	DoubleKeyword,
	FloatKeyword,
	IntKeyword,
	LongKeyword,
	ObjectKeyword,
	SByteKeyword,
	ShortKeyword,
	StringKeyword,
	UShortKeyword,
	UIntKeyword,
	ULongKeyword
}

public enum eOpCode
{
	IdentifierName,
	GenericName,
	InvocationExpression,
	ObjectCreationExpression,
	ElementAccessExpression,
	ElementBindingExpression,
	NumericLiteralExpression,
	StringLiteralExpression,
	InterpolatedStringText,
	InterpolatedStringExpression,
	CharacterLiteralExpression,
	PredefinedType,
	QualifiedName,
	AliasQualifiedName,
	MemberBindingExpression,
	ConditionalExpression,
	SimpleMemberAccessExpression,
	PointerMemberAccessExpression,
	CastExpression,
	AsExpression,
	AddExpression,
	MultiplyExpression,
	SubtractExpression,
	DivideExpression,
	ModuloExpression,
	LeftShiftExpression,
	RightShiftExpression,
	BitwiseAndExpression,
	BitwiseOrExpression,
	ExclusiveOrExpression,
	LogicalAndExpression,
	LogicalOrExpression,
	EqualsExpression,
	NotEqualsExpression,
	GreaterThanExpression,
	LessThanExpression,
	GreaterThanOrEqualExpression,
	LessThanOrEqualExpression,
	IsExpression,
	UnaryPlusExpression,
	UnaryMinusExpression,
	LogicalNotExpression,
	BitwiseNotExpression,
	TrueLiteralExpression,
	FalseLiteralExpression,
	NullLiteralExpression,
	PreIncrementExpression,
	PostIncrementExpression,
	PreDecrementExpression,
	PostDecrementExpression,
	SizeOfExpression,
	TypeOfExpression,
	CoalesceExpression,
	ThisExpression
}



public class SyntaxKindNotImplementedException : NotImplementedException
{
	public SyntaxKindNotImplementedException()
	{
	}

	public SyntaxKindNotImplementedException(string message)
		: base(message)
	{
	}

	public SyntaxKindNotImplementedException(string message, Exception inner)
		: base(message, inner)
	{
	}
}
