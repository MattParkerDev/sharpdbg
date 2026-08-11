using DebuggableConsoleApp.Namespace1;

namespace DebuggableConsoleApp;

public class MyClass : MyClassBase
{
	private readonly string _name = "TestName";
	private static int _counter = 1;
	public void MyMethod(long myParam, int myIntParam)
	{
		var myInt = 4;
		var enumVar = MyEnum.SecondValue;
		var enumWithFlagsVar = MyEnumWithFlags.FlagValue1 | MyEnumWithFlags.FlagValue3;
		var structVar = new MyStruct { Id = 5, Name = "StructName" };
		Console.WriteLine($"Log_MyMethod");
		_counter = 1;
		int? nullableInt;
		int? nullableIntWithVal = 4;
		MyClass? nullableRefType;
		AnotherClass.MyStaticMethod();
		var anotherVar = "asdf";
		;
	}
	//private const int nq = -1;

	//private MyClass2 get_ClassProperty() => ClassProperty;
	private MyClass3 _classField = new MyClass3();
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
	private static DateTime _utcNow = new(2026, 6, 13, 7, 18, 38);
	private static DateTime? _nullableUtcNow = new DateTime(2026, 6, 13, 7, 18, 38);
	private int _instanceField = 5;
	private static int _instanceStaticField = 6;
	public int IntProperty { get; set; } = 10;
	private ClassWithDebugDisplay _classWithDebugDisplay = new ClassWithDebugDisplay();
	private ClassWithDebugDisplay2 _classWithDebugDisplay2 = new ClassWithDebugDisplay2();
	private ClassWithDebugDisplay3 _classWithDebugDisplay3 = new ClassWithDebugDisplay3();
	private MyClassWithGeneric<int> _myClassWithGeneric = new MyClassWithGeneric<int> { GenericItems = [42], GenericItemsField = [43] };
	private Dictionary<int, int> _intDictionary = new Dictionary<int, int>() { { 5, 50 }, { 10, 100 }, { 15, 150 } };
	private MyRecord1 _recordField = new MyRecord1(1, 2);
	private MyStruct _structField = new MyStruct();
	private int Get14() => 14;
	private MyClass2 GetObject() => ClassProperty;
	private int ClearClassFieldAndCollect()
	{
		EvaluationLifetimeTracker.Track(_classField);
		_classField = null!;
		GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
		return 0;
	}
	private bool WasClearedClassFieldReleased() => EvaluationLifetimeTracker.WasReleased();
	private int DoubleNumber(int number) => number * 2;
	private float DoubleNumber(float number) => number * 2;
	private int TestMethod(int myInt, string myString) => myInt + myString.Length;
	private Func<int, int> _doubler = x => x * 2;
	private int IncrementByRef(ref int value) { value += 3; return value; }
	private int AssignOut(out int value) { value = 42; return value; }
	private int AddOneIn(in int value) => value + 1;
	private int ReadObjectIn(in MyClass2 value) => value.IntProperty;
	private bool IsSameString(string left, string right) => ReferenceEquals(left, right);
	private ref MyClass3 GetObjectByReference() => ref _classField;
	private ref MyStruct GetStructByRef() => ref _structField;
	private bool TryGetObject(out MyClass2 value) { value = new MyClass2(); return true; }
}

public class MyClass2
{
	public string MyProperty { get; set; } = "Hello";
	public int IntField = 6;
	public int IntProperty { get; set; } = 6;
}

public class MyClass3
{
	public string MyProperty { get; set; } = "Hello";
	public int IntField = 6;
	public int IntProperty { get; set; } = 6;
	public int AddToIntField(int value) => IntField + value;
	public MyClassContainingAnotherClass.MyNestedClass NestedClassProperty { get; set; } = new();
	public MyGenericClassContainingAnotherGenericClass<string, int>.MyNestedGenericClass<long, float> NestedGenericClassProperty { get; set; } = new();
}

internal static class EvaluationLifetimeTracker
{
	private static WeakReference<MyClass3>? _reference;

	public static void Track(MyClass3 value) => _reference = new WeakReference<MyClass3>(value);

	public static bool WasReleased()
	{
		GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
		return _reference is not null && !_reference.TryGetTarget(out _);
	}
}

public enum MyEnum
{
	FirstValue,
	SecondValue,
	ThirdValue
}

public record MyRecord1(int X, int Y);
