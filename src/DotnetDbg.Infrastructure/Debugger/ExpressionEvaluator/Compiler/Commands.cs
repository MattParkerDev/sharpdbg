using System.Text;
using DotnetDbg.Infrastructure.Debugger.Eval;
using Microsoft.CodeAnalysis.CSharp;
using static DotnetDbg.Infrastructure.Debugger.ExpressionEvaluator.Compiler.CompilerConstants;

namespace DotnetDbg.Infrastructure.Debugger.ExpressionEvaluator.Compiler;

public abstract class CommandBase
{
	public eOpCode OpCode { get; protected set; }
	public uint Flags { get; protected set; }
}

public class NoOperandsCommand : CommandBase
{
	public NoOperandsCommand(SyntaxKind kind, uint flags)
	{
		OpCode = KindAlias[kind];
		Flags = flags;
	}

	public override string ToString()
	{
		StringBuilder sb = new StringBuilder();
		sb.AppendFormat("{0}    flags={1}", OpCode, Flags);
		return sb.ToString();
	}
}

public class OneOperandCommand : CommandBase
{
	public dynamic Argument;

	public OneOperandCommand(SyntaxKind kind, uint flags, dynamic arg)
	{
		OpCode = KindAlias[kind];
		Flags = flags;
		Argument = arg;
	}

	public override string ToString()
	{
		StringBuilder sb = new StringBuilder();
		sb.AppendFormat("{0}    flags={1}    {2}", OpCode, Flags, Argument);
		return sb.ToString();
	}
}

public class TwoOperandCommand : CommandBase
{
	public dynamic[] Arguments;

	public TwoOperandCommand(SyntaxKind kind, uint flags, params dynamic[] args)
	{
		OpCode = KindAlias[kind];
		Flags = flags;
		Arguments = args;
	}

	public override string ToString()
	{
		StringBuilder sb = new StringBuilder();
		sb.AppendFormat("{0}    flags={1}", OpCode, Flags);
		foreach (var arg in Arguments)
		{
			sb.AppendFormat("    {0}", arg);
		}
		return sb.ToString();
	}
}
