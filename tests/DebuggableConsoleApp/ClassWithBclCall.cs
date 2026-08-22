namespace DebuggableConsoleApp;

public static class ClassWithBclCall
{
	public static void Test(int myParam)
	{
		var array = Enumerable.Range(1, 5).Select(Selector).ToArray();
	}

	private static int Selector(int x)
	{
		return x * 2;
	}
}
