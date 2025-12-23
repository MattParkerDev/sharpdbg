namespace DotnetDbg.Infrastructure.Debugger.Eval;

public partial class Evaluation
{
	public class StackMachineProgram
	{
		public static readonly int ProgramFinished = -1;
		public static readonly int BeforeFirstCommand = -2;
		public int CurrentPosition = BeforeFirstCommand;
		public List<ICommand> Commands = new List<ICommand>();
	}
}
