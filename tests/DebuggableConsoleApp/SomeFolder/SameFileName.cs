namespace DebuggableConsoleApp.SomeFolder;

// Same name as ../SameFileName.cs, which breakpoints set in tests must not bind to
public static class SameFileName
{
	public static void Test()
	{
		var value = 0;
		value++;
		value++;
		value++;
		value++;
		value++;
		value++;
		value++;
		value++;
	}
}
