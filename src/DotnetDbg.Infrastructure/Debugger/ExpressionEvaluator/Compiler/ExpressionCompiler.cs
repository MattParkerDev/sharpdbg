using DotnetDbg.Infrastructure.Debugger.ExpressionEvaluator.Interpreter;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace DotnetDbg.Infrastructure.Debugger.ExpressionEvaluator.Compiler;

public static class ExpressionCompiler
{
	public static CompiledExpression Compile(string expression)
	{
		var fixedExpression = CompiledExpressionInterpreter.ReplaceInternalNames(expression, false);
		var instructions = CompileInternal(fixedExpression);
		var compiledExpression = new CompiledExpression(instructions);
		return compiledExpression;
	}

	private static List<CommandBase> CompileInternal(string expression)
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

		var instructions = new List<CommandBase>();
		var treeWalker = new ExpressionSyntaxVisitor(instructions);
		treeWalker.Visit(tree.GetRoot());

		if (treeWalker.ExpressionStatementCount != 1)
		{
			throw new ArgumentException(treeWalker.ExpressionStatementCount > 1
				? "Only one expression must be provided"
				: "No expression found");
		}

		return instructions;
	}
}
