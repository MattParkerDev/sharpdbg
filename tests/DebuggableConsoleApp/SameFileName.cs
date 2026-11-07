namespace DebuggableConsoleApp;

// SomeFolder/SameFileName.cs has the same name, so breakpoint paths must be told apart by more than the file name
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
