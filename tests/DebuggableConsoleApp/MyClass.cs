namespace DebuggableConsoleApp;

public class MyClass
{
	private readonly string _name = "TestName";
	private static int _counter = 1;
	public void MyMethod(long myParam, int myIntParam)
	{
		var myInt = 4;
		var enumVar = MyEnum.SecondValue;
		var enumWithFlagsVar = MyEnumWithFlags.FlagValue1 | MyEnumWithFlags.FlagValue3;
		var structVar = new MyStruct { Id = 5, Name = "StructName" };
		Console.WriteLine($"Log{_counter:D}");
		_counter++;
		int? nullableInt;
		int? nullableIntWithVal = 4;
		MyClass? nullableRefType;

		var anotherVar = "asdf";
		;
	}
	//private const int nq = -1;

	//private MyClass2 get_ClassProperty() => ClassProperty;
	private MyClass2 ClassProperty { get; set; } = new MyClass2();
	private MyClass2 ClassProperty2 { get; set; } = new MyClass2();
	private static int IntStaticProperty { get; set; } = 10;
	private static MyClass2 StaticClassProperty { get; set; } = new MyClass2();
	//private static MyClass2 get_StaticClassProperty() => StaticClassProperty;
	private static MyClass2 _staticClassField = new MyClass2();
	private List<int> _intList = [1, 4, 8, 25];
	private int[] _intArray = [2, 3, 5, 7];
	private static List<int> _staticIntList = [1, 4, 8, 25];
	private static Dictionary<MyClass2, MyClass> _fieldDictionary = [];
	private static DateTime _utcNow = DateTime.UtcNow;
	private static DateTime? _nullableUtcNow = DateTime.UtcNow;
	private int _instanceField = 5;
	private static int _instanceStaticField = 6;
	public int IntProperty { get; set; } = 10;
	private ClassWithDebugDisplay _classWithDebugDisplay = new ClassWithDebugDisplay();
	private MyClassWithGeneric<int> _myClassWithGeneric = new MyClassWithGeneric<int> { GenericItems = [42] };
	private int Get14() => 14;
	private int DoubleNumber(int number) => number * 2;
	private float DoubleNumber(float number) => number * 2;
	private int TestMethod(int myInt, string myString) => myInt + myString.Length;
}

public class MyClass2
{
	public string MyProperty { get; set; } = "Hello";
	public int IntField = 6;
	public int IntProperty { get; set; } = 6;
}

public enum MyEnum
{
	FirstValue,
	SecondValue,
	ThirdValue
}
