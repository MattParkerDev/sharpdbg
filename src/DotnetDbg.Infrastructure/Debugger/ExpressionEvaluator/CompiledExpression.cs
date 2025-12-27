using DotnetDbg.Infrastructure.Debugger.ExpressionEvaluator.Compiler;

namespace DotnetDbg.Infrastructure.Debugger.ExpressionEvaluator;

public class CompiledExpression(List<CommandBase> instructions)
{
	public List<CommandBase> Instructions { get; set; } = instructions;
}
