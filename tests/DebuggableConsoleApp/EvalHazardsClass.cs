namespace DebuggableConsoleApp;

// Members that are only ever run by the debugger's evaluator
public class EvalHazardsClass
{
	private static bool _block = true;

	public int GetterWithBreakpoint
	{
		get
		{
			var value = 5;
			return value;
		}
	}

	public int BlockingGetter
	{
		get
		{
			while (_block) Thread.Sleep(10);
			return 0;
		}
	}

	public int ExitProcess()
	{
		Environment.Exit(3);
		return 0;
	}

	public static void Test()
	{
		var hazards = new EvalHazardsClass();
		;
	}
}
