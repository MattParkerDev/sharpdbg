using System.Diagnostics;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace DotnetDbg.Infrastructure.Debugger.Eval;

public partial class Evaluation
{

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

	static readonly Dictionary<Type, ePredefinedType> TypeAlias = new Dictionary<Type, ePredefinedType>
	{
		{ typeof(bool), ePredefinedType.BoolKeyword },
		{ typeof(byte), ePredefinedType.ByteKeyword },
		{ typeof(char), ePredefinedType.CharKeyword },
		{ typeof(decimal), ePredefinedType.DecimalKeyword },
		{ typeof(double), ePredefinedType.DoubleKeyword },
		{ typeof(float), ePredefinedType.FloatKeyword },
		{ typeof(int), ePredefinedType.IntKeyword },
		{ typeof(long), ePredefinedType.LongKeyword },
		{ typeof(object), ePredefinedType.ObjectKeyword },
		{ typeof(sbyte), ePredefinedType.SByteKeyword },
		{ typeof(short), ePredefinedType.ShortKeyword },
		{ typeof(string), ePredefinedType.StringKeyword },
		{ typeof(ushort), ePredefinedType.UShortKeyword },
		{ typeof(uint), ePredefinedType.UIntKeyword },
		{ typeof(ulong), ePredefinedType.ULongKeyword }
	};

	static readonly Dictionary<SyntaxKind, ePredefinedType> TypeKindAlias = new Dictionary<SyntaxKind, ePredefinedType>
	{
		{ SyntaxKind.BoolKeyword,    ePredefinedType.BoolKeyword },
		{ SyntaxKind.ByteKeyword,    ePredefinedType.ByteKeyword },
		{ SyntaxKind.CharKeyword,    ePredefinedType.CharKeyword },
		{ SyntaxKind.DecimalKeyword, ePredefinedType.DecimalKeyword },
		{ SyntaxKind.DoubleKeyword,  ePredefinedType.DoubleKeyword },
		{ SyntaxKind.FloatKeyword,   ePredefinedType.FloatKeyword },
		{ SyntaxKind.IntKeyword,     ePredefinedType.IntKeyword },
		{ SyntaxKind.LongKeyword,    ePredefinedType.LongKeyword },
		{ SyntaxKind.ObjectKeyword,  ePredefinedType.ObjectKeyword },
		{ SyntaxKind.SByteKeyword,   ePredefinedType.SByteKeyword },
		{ SyntaxKind.ShortKeyword,   ePredefinedType.ShortKeyword },
		{ SyntaxKind.StringKeyword,  ePredefinedType.StringKeyword },
		{ SyntaxKind.UShortKeyword,  ePredefinedType.UShortKeyword },
		{ SyntaxKind.UIntKeyword,    ePredefinedType.UIntKeyword },
		{ SyntaxKind.ULongKeyword,   ePredefinedType.ULongKeyword }
	};

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

	static readonly Dictionary<SyntaxKind, eOpCode> KindAlias = new Dictionary<SyntaxKind, eOpCode>
	{
		{ SyntaxKind.IdentifierName,                eOpCode.IdentifierName },
		{ SyntaxKind.GenericName,                   eOpCode.GenericName },
		{ SyntaxKind.InvocationExpression,          eOpCode.InvocationExpression },
		{ SyntaxKind.ObjectCreationExpression,      eOpCode.ObjectCreationExpression },
		{ SyntaxKind.ElementAccessExpression,       eOpCode.ElementAccessExpression },
		{ SyntaxKind.ElementBindingExpression,      eOpCode.ElementBindingExpression },
		{ SyntaxKind.NumericLiteralExpression,      eOpCode.NumericLiteralExpression },
		{ SyntaxKind.StringLiteralExpression,       eOpCode.StringLiteralExpression },
		{ SyntaxKind.CharacterLiteralExpression,    eOpCode.CharacterLiteralExpression },
		{ SyntaxKind.PredefinedType,                eOpCode.PredefinedType },
		{ SyntaxKind.QualifiedName,                 eOpCode.QualifiedName },
		{ SyntaxKind.AliasQualifiedName,            eOpCode.AliasQualifiedName },
		{ SyntaxKind.MemberBindingExpression,       eOpCode.MemberBindingExpression },
		{ SyntaxKind.ConditionalExpression,         eOpCode.ConditionalExpression },
		{ SyntaxKind.SimpleMemberAccessExpression,  eOpCode.SimpleMemberAccessExpression },
		{ SyntaxKind.PointerMemberAccessExpression, eOpCode.PointerMemberAccessExpression },
		{ SyntaxKind.CastExpression,                eOpCode.CastExpression },
		{ SyntaxKind.AsExpression,                  eOpCode.AsExpression },
		{ SyntaxKind.AddExpression,                 eOpCode.AddExpression },
		{ SyntaxKind.MultiplyExpression,            eOpCode.MultiplyExpression },
		{ SyntaxKind.SubtractExpression,            eOpCode.SubtractExpression },
		{ SyntaxKind.DivideExpression,              eOpCode.DivideExpression },
		{ SyntaxKind.ModuloExpression,              eOpCode.ModuloExpression },
		{ SyntaxKind.LeftShiftExpression,           eOpCode.LeftShiftExpression },
		{ SyntaxKind.RightShiftExpression,          eOpCode.RightShiftExpression },
		{ SyntaxKind.BitwiseAndExpression,          eOpCode.BitwiseAndExpression },
		{ SyntaxKind.BitwiseOrExpression,           eOpCode.BitwiseOrExpression },
		{ SyntaxKind.ExclusiveOrExpression,         eOpCode.ExclusiveOrExpression },
		{ SyntaxKind.LogicalAndExpression,          eOpCode.LogicalAndExpression },
		{ SyntaxKind.LogicalOrExpression,           eOpCode.LogicalOrExpression },
		{ SyntaxKind.EqualsExpression,              eOpCode.EqualsExpression },
		{ SyntaxKind.NotEqualsExpression,           eOpCode.NotEqualsExpression },
		{ SyntaxKind.GreaterThanExpression,         eOpCode.GreaterThanExpression },
		{ SyntaxKind.LessThanExpression,            eOpCode.LessThanExpression },
		{ SyntaxKind.GreaterThanOrEqualExpression,  eOpCode.GreaterThanOrEqualExpression },
		{ SyntaxKind.LessThanOrEqualExpression,     eOpCode.LessThanOrEqualExpression },
		{ SyntaxKind.IsExpression,                  eOpCode.IsExpression },
		{ SyntaxKind.UnaryPlusExpression,           eOpCode.UnaryPlusExpression },
		{ SyntaxKind.UnaryMinusExpression,          eOpCode.UnaryMinusExpression },
		{ SyntaxKind.LogicalNotExpression,          eOpCode.LogicalNotExpression },
		{ SyntaxKind.BitwiseNotExpression,          eOpCode.BitwiseNotExpression },
		{ SyntaxKind.TrueLiteralExpression,         eOpCode.TrueLiteralExpression },
		{ SyntaxKind.FalseLiteralExpression,        eOpCode.FalseLiteralExpression },
		{ SyntaxKind.NullLiteralExpression,         eOpCode.NullLiteralExpression },
		{ SyntaxKind.PreIncrementExpression,        eOpCode.PreIncrementExpression },
		{ SyntaxKind.PostIncrementExpression,       eOpCode.PostIncrementExpression },
		{ SyntaxKind.PreDecrementExpression,        eOpCode.PreDecrementExpression },
		{ SyntaxKind.PostDecrementExpression,       eOpCode.PostDecrementExpression },
		{ SyntaxKind.SizeOfExpression,              eOpCode.SizeOfExpression },
		{ SyntaxKind.TypeOfExpression,              eOpCode.TypeOfExpression },
		{ SyntaxKind.CoalesceExpression,            eOpCode.CoalesceExpression },
		{ SyntaxKind.ThisExpression,                eOpCode.ThisExpression }
	};

	public abstract class ICommand
	{
		public eOpCode OpCode { get; protected set; }
		public uint Flags { get; protected set; }
	}

	public class NoOperandsCommand : ICommand
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

	public class OneOperandCommand : ICommand
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

	public class TwoOperandCommand : ICommand
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

	public static StackMachineProgram GenerateStackMachineProgram(string expression)
	{
		var parseOptions = CSharpParseOptions.Default.WithKind(SourceCodeKind.Script);
		var tree = CSharpSyntaxTree.ParseText(expression, parseOptions);

		var parseErrors = tree.GetDiagnostics(tree.GetRoot());
		var errors = new List<string>();
		foreach (var error in parseErrors)
		{
			if (error.Severity == DiagnosticSeverity.Error)
				errors.Add($"error {error.Id}: {error.GetMessage()}");
		}

		if (errors.Count > 0)
		{
			throw new ArgumentException(string.Join("\n", errors));
		}

		var treeWalker = new TreeWalker();
		treeWalker.Visit(tree.GetRoot());

		if (treeWalker.ExpressionStatementCount != 1)
		{
			throw new ArgumentException(treeWalker.ExpressionStatementCount > 1
				? "Only one expression must be provided"
				: "No expression found");
		}

		return treeWalker.stackMachineProgram;
	}
}
