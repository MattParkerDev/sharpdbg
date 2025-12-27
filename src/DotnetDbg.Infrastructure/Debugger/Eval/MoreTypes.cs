namespace DotnetDbg.Infrastructure.Debugger.Eval;

public class SyntaxKindNotImplementedException : NotImplementedException
{
	public SyntaxKindNotImplementedException()
	{
	}

	public SyntaxKindNotImplementedException(string message)
		: base(message)
	{
	}

	public SyntaxKindNotImplementedException(string message, Exception inner)
		: base(message, inner)
	{
	}
}
