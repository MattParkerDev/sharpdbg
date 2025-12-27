using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using static DotnetDbg.Infrastructure.Debugger.Eval.EvalConstants;

namespace DotnetDbg.Infrastructure.Debugger.Eval;

public class TreeWalker : CSharpSyntaxWalker
{
	bool ExpressionStatementBody = false;
	public int ExpressionStatementCount = 0;
#if DEBUG_STACKMACHINE
        // Gather AST data for DebugText.
        List<string> ST = new List<string>();
        int CurrentNodeDepth = 0;
#endif

	// CheckedExpression/UncheckedExpression syntax kind related
	static readonly uint maskChecked = 0xFFFFFFFE;
	static readonly uint flagChecked = 0x00000001;
	static readonly uint flagUnchecked = 0x00000000;
	// Tracking current AST scope flags.
	static readonly uint defaultScopeFlags = flagUnchecked;
	Stack<uint> CurrentScopeFlags = new Stack<uint>();

	public StackMachineProgram stackMachineProgram = new StackMachineProgram();

	public override void Visit(SyntaxNode? node)
	{
		if (node is null) throw new ArgumentNullException(nameof(node));
		if (node.IsKind(SyntaxKind.ExpressionStatement))
		{
			ExpressionStatementCount++;
			ExpressionStatementBody = true;
			base.Visit(node);
			ExpressionStatementBody = false;
		}
		else if (ExpressionStatementBody)
		{
			// Setup flags before base.Visit() call, since all nested Kinds must have proper flags.
			if (CurrentScopeFlags.Count == 0)
			{
				CurrentScopeFlags.Push(defaultScopeFlags);
			}
			else
			{
				CurrentScopeFlags.Push(CurrentScopeFlags.Peek());
			}

			switch (node.Kind())
			{
				case SyntaxKind.UncheckedExpression:
					CurrentScopeFlags.Push((CurrentScopeFlags.Pop() & maskChecked) | flagUnchecked);
					break;
				case SyntaxKind.CheckedExpression:
					CurrentScopeFlags.Push((CurrentScopeFlags.Pop() & maskChecked) | flagChecked);
					break;
			}
#if DEBUG_STACKMACHINE
                CurrentNodeDepth++;

                // Gather AST data for DebugText.
                var indents = new String(' ', CurrentNodeDepth * 4);
                ST.Add(indents + node.Kind() + " --- " + node.ToString());
#endif
			// Visit nested Kinds in proper order.
			// Note, we should setup flags before and parse Kinds after this call.
			base.Visit(node);
#if DEBUG_STACKMACHINE
                CurrentNodeDepth--;
#endif
			switch (node.Kind())
			{
				/*
				DefaultExpression - should not be in expression AST
				*/

				case SyntaxKind.IdentifierName:
				case SyntaxKind.StringLiteralExpression:
				case SyntaxKind.InterpolatedStringText:
					stackMachineProgram.Commands.Add(new OneOperandCommand(node.Kind(), CurrentScopeFlags.Peek(), node.GetFirstToken().Value));
					break;

				case SyntaxKind.InterpolatedStringExpression:
					int? InterpolatedStringContentCount = null;
					foreach (var child in node.ChildNodes())
					{
						if (!child.IsKind(SyntaxKind.InterpolatedStringText) &&
							!child.IsKind(SyntaxKind.Interpolation))
							continue;

						InterpolatedStringContentCount ??= 0;
						InterpolatedStringContentCount++;
					}
					if (InterpolatedStringContentCount == null || InterpolatedStringContentCount < 1)
					{
						throw new ArgumentOutOfRangeException(node.Kind() + " must have at least one content element!");
					}
					stackMachineProgram.Commands.Add(new OneOperandCommand(node.Kind(), CurrentScopeFlags.Peek(), InterpolatedStringContentCount));
					break;

				case SyntaxKind.GenericName:
					// GenericName
					//     \ TypeArgumentList
					//           \ types OR OmittedTypeArgument
					int? GenericNameArgs = null;
					bool OmittedTypeArg = false;
					foreach (var child in node.ChildNodes())
					{
						if (!Microsoft.CodeAnalysis.CSharpExtensions.IsKind(child, SyntaxKind.TypeArgumentList))
							continue;

						GenericNameArgs = 0;

						foreach (var ArgumentListChild in child.ChildNodes())
						{
							if (Microsoft.CodeAnalysis.CSharpExtensions.IsKind(ArgumentListChild, SyntaxKind.OmittedTypeArgument))
							{
								OmittedTypeArg = true;
								break;
							}

							GenericNameArgs++;
						}
					}
					if (GenericNameArgs == null || (GenericNameArgs < 1 && !OmittedTypeArg))
					{
						throw new ArgumentOutOfRangeException(node.Kind() + " must have at least one type!");
					}
					stackMachineProgram.Commands.Add(new TwoOperandCommand(node.Kind(), CurrentScopeFlags.Peek(), node.GetFirstToken().Value, GenericNameArgs));
					break;

				case SyntaxKind.InvocationExpression:
/* TODO
                    case SyntaxKind.ObjectCreationExpression:
*/
					// InvocationExpression/ObjectCreationExpression
					//     \ ArgumentList
					//           \ Argument
					int? ArgsCount = null;
					foreach (var child in node.ChildNodes())
					{
						if (!Microsoft.CodeAnalysis.CSharpExtensions.IsKind(child, SyntaxKind.ArgumentList))
							continue;

						ArgsCount = new int();

						foreach (var ArgumentListChild in child.ChildNodes())
						{
							if (!Microsoft.CodeAnalysis.CSharpExtensions.IsKind(ArgumentListChild, SyntaxKind.Argument))
								continue;

							ArgsCount++;
						}
					}
					if (ArgsCount == null)
					{
						throw new ArgumentOutOfRangeException(node.Kind() + " must have at least one argument!");
					}
					stackMachineProgram.Commands.Add(new OneOperandCommand(node.Kind(), CurrentScopeFlags.Peek(), ArgsCount));
					break;

				case SyntaxKind.ElementAccessExpression:
				case SyntaxKind.ElementBindingExpression:
					// ElementAccessExpression/ElementBindingExpression
					//     \ BracketedArgumentList
					//           \ Argument
					int? ElementAccessArgs = null;
					foreach (var child in node.ChildNodes())
					{
						if (!Microsoft.CodeAnalysis.CSharpExtensions.IsKind(child, SyntaxKind.BracketedArgumentList))
							continue;

						ElementAccessArgs = new int();

						foreach (var ArgumentListChild in child.ChildNodes())
						{
							if (!Microsoft.CodeAnalysis.CSharpExtensions.IsKind(ArgumentListChild, SyntaxKind.Argument))
								continue;

							ElementAccessArgs++;
						}
					}
					if (ElementAccessArgs == null)
					{
						throw new ArgumentOutOfRangeException(node.Kind() + " must have at least one argument!");
					}
					stackMachineProgram.Commands.Add(new OneOperandCommand(node.Kind(), CurrentScopeFlags.Peek(), ElementAccessArgs));
					break;

				case SyntaxKind.NumericLiteralExpression:
				case SyntaxKind.CharacterLiteralExpression: // 1 wchar
					stackMachineProgram.Commands.Add(new TwoOperandCommand(node.Kind(), CurrentScopeFlags.Peek(), TypeAlias[node.GetFirstToken().Value.GetType()], node.GetFirstToken().Value));
					break;

				case SyntaxKind.PredefinedType:
					stackMachineProgram.Commands.Add(new OneOperandCommand(node.Kind(), CurrentScopeFlags.Peek(), TypeKindAlias[node.GetFirstToken().Kind()]));
					break;

				// skip, in case of stack machine program creation we don't use this kinds directly
				case SyntaxKind.Argument:
				case SyntaxKind.Interpolation:
				case SyntaxKind.BracketedArgumentList:
				case SyntaxKind.ConditionalAccessExpression:
				case SyntaxKind.ArgumentList:
				case SyntaxKind.ParenthesizedExpression:
				case SyntaxKind.TypeArgumentList:
/* TODO
                    case SyntaxKind.OmittedTypeArgument:
                    case SyntaxKind.UncheckedExpression:
                    case SyntaxKind.CheckedExpression:
*/
					break;

				case SyntaxKind.SimpleMemberAccessExpression:
				case SyntaxKind.TrueLiteralExpression:
				case SyntaxKind.FalseLiteralExpression:
				case SyntaxKind.NullLiteralExpression:
				case SyntaxKind.ThisExpression:
				case SyntaxKind.MemberBindingExpression:
				case SyntaxKind.UnaryPlusExpression:
				case SyntaxKind.UnaryMinusExpression:
				case SyntaxKind.AddExpression:
				case SyntaxKind.MultiplyExpression:
				case SyntaxKind.SubtractExpression:
				case SyntaxKind.DivideExpression:
				case SyntaxKind.ModuloExpression:
				case SyntaxKind.RightShiftExpression:
				case SyntaxKind.LeftShiftExpression:
				case SyntaxKind.BitwiseNotExpression:
				case SyntaxKind.LogicalAndExpression:
				case SyntaxKind.LogicalOrExpression:
				case SyntaxKind.ExclusiveOrExpression:
				case SyntaxKind.BitwiseAndExpression:
				case SyntaxKind.BitwiseOrExpression:
				case SyntaxKind.LogicalNotExpression:
				case SyntaxKind.EqualsExpression:
				case SyntaxKind.NotEqualsExpression:
				case SyntaxKind.GreaterThanExpression:
				case SyntaxKind.LessThanExpression:
				case SyntaxKind.GreaterThanOrEqualExpression:
				case SyntaxKind.LessThanOrEqualExpression:
				case SyntaxKind.QualifiedName:
				case SyntaxKind.CoalesceExpression:

/* TODO
                    case SyntaxKind.AliasQualifiedName:
                    case SyntaxKind.ConditionalExpression:
                    case SyntaxKind.PointerMemberAccessExpression:
                    case SyntaxKind.CastExpression:
                    case SyntaxKind.AsExpression:
                    case SyntaxKind.IsExpression:
                    case SyntaxKind.PreIncrementExpression:
                    case SyntaxKind.PostIncrementExpression:
                    case SyntaxKind.PreDecrementExpression:
                    case SyntaxKind.PostDecrementExpression:
*/
				case SyntaxKind.SizeOfExpression:
/*
                    case SyntaxKind.TypeOfExpression:
*/
					stackMachineProgram.Commands.Add(new NoOperandsCommand(node.Kind(), CurrentScopeFlags.Peek()));
					break;

				default:
					throw new SyntaxKindNotImplementedException(node.Kind() + " not implemented!");
			}

			CurrentScopeFlags.Pop();
		}
		else
		{
			// skip CompilationUnit, GlobalStatement and ExpressionStatement kinds
			base.Visit(node);
		}
	}

#if DEBUG_STACKMACHINE
        public string GenerateDebugText()
        {
            // We cannot derive from sealed type 'StringBuilder' and it use platform-dependant Environment.NewLine for new line.
            // Use '\n' directly, since netcoredbg use only '\n' for new line.
            StringBuilder sb = new StringBuilder();
            sb.Append("=======================================\n");
            sb.Append("Source tree:\n");
            foreach (var line in ST)
            {
                sb.AppendFormat("{0}\n", line);
            }
            sb.Append("=======================================\n");
            sb.Append("Stack machine commands:\n");
            foreach (var command in stackMachineProgram.Commands)
            {
                sb.AppendFormat("    {0}\n", command.ToString());
            }
            return sb.ToString();
        }
#endif
}
