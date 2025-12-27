using ClrDebug;
using DotnetDbg.Infrastructure.Debugger.Eval;

namespace DotnetDbg.Infrastructure.Debugger.ExpressionEvaluator.Interpreter;

public class EvalStackEntry
{
	public CorDebugValue? CorDebugValue { get; set; }
	public List<string> Identifiers { get; set; } = new();
	public List<CorDebugType?>? GenericTypeCache { get; set; }
	public bool Literal { get; set; }
	public bool Editable { get; set; }
	public bool PreventBinding { get; set; }
	public SetterData? SetterData { get; set; }

	public void ResetEntry()
	{
		CorDebugValue = null;
		Identifiers.Clear();
		GenericTypeCache = null;
		Literal = false;
	}

	public void ResetEntry(ResetLiteralStatus resetLiteral)
	{
		CorDebugValue = null;
		Identifiers.Clear();
		GenericTypeCache = null;
		if (resetLiteral == ResetLiteralStatus.Yes)
		{
			Literal = false;
		}
	}

	public enum ResetLiteralStatus
	{
		Yes,
		No
	}
}
