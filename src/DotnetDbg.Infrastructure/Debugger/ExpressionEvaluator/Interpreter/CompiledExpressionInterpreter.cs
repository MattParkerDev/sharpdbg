using DotnetDbg.Infrastructure.Debugger.Eval;

namespace DotnetDbg.Infrastructure.Debugger.ExpressionEvaluator.Interpreter;

public class CompiledExpressionInterpreter
{
	public async Task<EvaluationResult> Interpret(CompiledExpression compiledExpression, CompiledExpressionEvaluationContext context)
	{
		var old = new StackMachineLegacy(context.EvalData, context.Debugger);
		var result = await old.Run(compiledExpression.Instructions);
		return result;
	}
}
