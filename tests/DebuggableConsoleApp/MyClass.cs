namespace DebuggableConsoleApp;

public static class MyClass
{
	private static int _counter = 0;
	public static void MyMethod()
	{
		Console.WriteLine($"Log{_counter}");
		_counter++;
	}
}
