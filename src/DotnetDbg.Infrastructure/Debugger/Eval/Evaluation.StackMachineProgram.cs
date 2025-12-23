using System.Collections;
using System.Collections.Generic;

namespace DotnetDbg.Infrastructure.Debugger.Eval;

public partial class Evaluation
{
	public class StackMachineProgram : IEnumerable<ICommand>
	{
		public List<ICommand> Commands = new List<ICommand>();

		public IEnumerator<ICommand> GetEnumerator()
		{
			return Commands.GetEnumerator();
		}

		IEnumerator IEnumerable.GetEnumerator()
		{
			return GetEnumerator();
		}
	}
}
