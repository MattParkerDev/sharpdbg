namespace DebuggableConsoleApp;

public class MyClass
{
	private readonly string _name = "TestName";
	private static int _counter = 1;
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

	//private MyClass2 get_ClassProperty() => ClassProperty;
	private MyClass2 ClassProperty { get; set; } = new MyClass2();
	private MyClass2 ClassProperty2 { get; set; } = new MyClass2();
	private static int InyStaticProperty { get; set; } = 10;
	private static MyClass2 StaticClassProperty { get; set; } = new MyClass2();
	//private static MyClass2 get_StaticClassProperty() => StaticClassProperty;
	private static MyClass2 _staticClassField = new MyClass2();
	private List<int> _intList = [1, 4, 8, 25];
	private static List<int> _staticIntList = [1, 4, 8, 25];
	private static Dictionary<MyClass2, MyClass> _fieldDictionary = [];
	private static DateTime _utcNow = DateTime.UtcNow;
}

public class MyClass2
{
	public string MyProperty { get; set; } = "Hello";
}
