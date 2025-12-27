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

public abstract class CommandBase
{
	public eOpCode OpCode { get; protected set; }
	public uint Flags { get; protected set; }
}

public class NoOperandsCommand : CommandBase
{
	public NoOperandsCommand(SyntaxKind kind, uint flags)
	{
		OpCode = KindAlias[kind];
		Flags = flags;
	}

	public override string ToString()
	{
		StringBuilder sb = new StringBuilder();
		sb.AppendFormat("{0}    flags={1}", OpCode, Flags);
		return sb.ToString();
	}
}

public class OneOperandCommand : CommandBase
{
	public dynamic Argument;

	public OneOperandCommand(SyntaxKind kind, uint flags, dynamic arg)
	{
		OpCode = KindAlias[kind];
		Flags = flags;
		Argument = arg;
	}

	public override string ToString()
	{
		StringBuilder sb = new StringBuilder();
		sb.AppendFormat("{0}    flags={1}    {2}", OpCode, Flags, Argument);
		return sb.ToString();
	}
}

public class TwoOperandCommand : CommandBase
{
	public dynamic[] Arguments;

	public TwoOperandCommand(SyntaxKind kind, uint flags, params dynamic[] args)
	{
		OpCode = KindAlias[kind];
		Flags = flags;
		Arguments = args;
	}

	public override string ToString()
	{
		StringBuilder sb = new StringBuilder();
		sb.AppendFormat("{0}    flags={1}", OpCode, Flags);
		foreach (var arg in Arguments)
		{
			sb.AppendFormat("    {0}", arg);
		}
		return sb.ToString();
	}
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
