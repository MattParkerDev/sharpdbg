using ClrDebug;
using System.Text;

namespace DotnetDbg.Infrastructure.Debugger.Eval;

public partial class StackMachineLegacy
{
	private readonly EvalData _evalData;
	private readonly ManagedDebugger _debugger;

	public StackMachineLegacy(EvalData evalData, ManagedDebugger debugger)
	{
		_evalData = evalData;
		_debugger = debugger;
	}


}

public class EvaluationResult
{
	public CorDebugValue? Value { get; set; }
	public bool Editable { get; set; }
	public SetterData? SetterData { get; set; }
	public string? Error { get; set; }
}
