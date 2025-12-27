using DotnetDbg.Infrastructure.Debugger.Eval;

namespace DotnetDbg.Infrastructure.Debugger.ExpressionEvaluator;

public class CompiledExpressionEvaluationContext
{
	public required ManagedDebugger Debugger { get; set; }
	public required EvalData EvalData { get; set; }
}
