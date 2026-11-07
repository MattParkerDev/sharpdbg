namespace DebuggableConsoleApp;

public class StepOverConditionalBreakpointClass
{
	public static void Test()
	{
		var total = 0;
		total += Add(1);
		total += Add(2);
	}

	private static int Add(int value)
	{
		return value + 1;
	}
}
