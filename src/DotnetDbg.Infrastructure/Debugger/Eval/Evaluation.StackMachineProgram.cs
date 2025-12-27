using System.Collections;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace DotnetDbg.Infrastructure.Debugger.Eval;

public class StackMachineProgram : IEnumerable<ICommand>
{
	public List<ICommand> Commands = new List<ICommand>();

	public IEnumerator<ICommand> GetEnumerator()
	{
		return Commands.GetEnumerator();
	}

	IEnumerator IEnumerable.GetEnumerator()
	{
		return GetEnumerator();
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
