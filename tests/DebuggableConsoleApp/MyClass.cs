namespace DebuggableConsoleApp;

public class MyClass
{
	private readonly string _name = "TestName";
	private static int _counter = 0;
	public void MyMethod(long myParam)
	{
		var myInt = 4;
		Console.WriteLine($"Log{_counter}");
		_counter++;
		int? nullableInt;
		int? nullableIntWithVal = 4;
		MyClass? nullableRefType;

		var anotherVar = "asdf";
	}
}
