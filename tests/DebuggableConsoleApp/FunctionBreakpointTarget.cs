namespace DebuggableConsoleApp;

public class FunctionBreakpointTarget
{
	public static void TargetMethod()
	{
		var localInTarget = "function-breakpoint-hit";
		Console.WriteLine($"Function breakpoint: {localInTarget}");
	}
}
