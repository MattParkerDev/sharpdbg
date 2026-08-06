using ICorDebugSharp;

namespace SharpDbg.Infrastructure.Debugger.ExpressionEvaluator.Interpreter;

public class EvalStackEntry
{
	public ICorDebugValue? CorDebugValue { get; set; }
	public List<string> Identifiers { get; set; } = new();
	public List<ICorDebugType>? GenericTypeCache { get; set; }
	public bool Literal { get; set; }
	public bool Editable { get; set; }
	public bool PreventBinding { get; set; }
	public bool IsByRefArgument { get; set; }

	public void ResetEntry()
	{
		CorDebugValue = null;
		Identifiers.Clear();
		GenericTypeCache = null;
		Literal = false;
		IsByRefArgument = false;
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
