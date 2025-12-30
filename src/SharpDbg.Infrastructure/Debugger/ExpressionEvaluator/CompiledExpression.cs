using SharpDbg.Infrastructure.Debugger.ExpressionEvaluator.Compiler;

namespace SharpDbg.Infrastructure.Debugger.ExpressionEvaluator;

public class CompiledExpression(List<CommandBase> instructions)
{
	public List<CommandBase> Instructions { get; set; } = instructions;
}
