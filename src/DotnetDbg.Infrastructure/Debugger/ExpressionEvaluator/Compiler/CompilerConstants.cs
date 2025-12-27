using Microsoft.CodeAnalysis.CSharp;

namespace DotnetDbg.Infrastructure.Debugger.ExpressionEvaluator.Compiler;

internal static class CompilerConstants
{
	public static readonly Dictionary<Type, ePredefinedType> TypeAlias = new Dictionary<Type, ePredefinedType>
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

	public static readonly Dictionary<SyntaxKind, ePredefinedType> TypeKindAlias = new Dictionary<SyntaxKind, ePredefinedType>
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

	public static readonly Dictionary<SyntaxKind, eOpCode> KindAlias = new Dictionary<SyntaxKind, eOpCode>
	{
		{ SyntaxKind.IdentifierName,                eOpCode.IdentifierName },
		{ SyntaxKind.GenericName,                   eOpCode.GenericName },
		{ SyntaxKind.InvocationExpression,          eOpCode.InvocationExpression },
		{ SyntaxKind.ObjectCreationExpression,      eOpCode.ObjectCreationExpression },
		{ SyntaxKind.ElementAccessExpression,       eOpCode.ElementAccessExpression },
		{ SyntaxKind.ElementBindingExpression,      eOpCode.ElementBindingExpression },
		{ SyntaxKind.NumericLiteralExpression,      eOpCode.NumericLiteralExpression },
		{ SyntaxKind.StringLiteralExpression,       eOpCode.StringLiteralExpression },
		{ SyntaxKind.InterpolatedStringText,        eOpCode.InterpolatedStringText },
		{ SyntaxKind.InterpolatedStringExpression,  eOpCode.InterpolatedStringExpression },
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
}
