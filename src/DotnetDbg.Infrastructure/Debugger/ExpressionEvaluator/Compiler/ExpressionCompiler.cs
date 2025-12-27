using DotnetDbg.Infrastructure.Debugger.Eval;

namespace DotnetDbg.Infrastructure.Debugger.ExpressionEvaluator.Compiler;

public static class ExpressionCompiler
{
	public static CompiledExpression Compile(string expression)
	{
		var fixedExpression = StackMachine.ReplaceInternalNames(expression, false);
		var old = StackMachineProgram.GenerateStackMachineProgram(fixedExpression);
		var compiledExpression = new CompiledExpression(old.Commands);
		return compiledExpression;
	}
}
