namespace DebuggableConsoleApp;

public class HitConditionClass
{
	private static int _count = 0;

	public void Test()
	{
		_count++;
		; // breakpoint here
	}
}
