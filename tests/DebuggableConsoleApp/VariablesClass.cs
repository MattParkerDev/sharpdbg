namespace DebuggableConsoleApp;

public class VariablesClass
{
	public bool BoolField = true;
	public byte ByteField = 1;
	public sbyte SByteField = -1;
	public short ShortField = -2;
	public ushort UShortField = 2;
	public int IntField = 123;
	public uint UIntField = 123u;
	public long LongField = 123456789L;
	public ulong ULongField = 123456789UL;

	public char CharField = 'A';

	public float FloatField = 1.23f;
	public double DoubleField = 2.34;
	public decimal DecimalField = 3.45m;

	public int? NullableIntField = 42;
	public int? NullableIntNullField = null;

	public bool? NullableBoolField = true;
	public bool? NullableBoolNullField = null;
	public Guid? NullableGuidField = Guid.NewGuid();

	public DayOfWeek? NullableEnumField = DayOfWeek.Friday;
	public DayOfWeek? NullableEnumNullField = null;

	public string StringField = "Hello";
	public string? NullableStringField = null;

	public object ObjectField = new object();
	public object? NullableObjectField = null;

	public int[] IntArrayField = [1, 2, 3];
	public string?[] NullableStringArrayField = ["A", null, "C"];
	public int[,] MultiDimArrayField =
	{
		{ 1, 2 },
		{ 3, 4 }
	};
	public int[][] JaggedArrayField =
	[
		[1, 2],
		[3, 4, 5]
	];

	public List<int> ListField = [1, 2, 3];
	public Dictionary<string, int> DictionaryField =
		new()
		{
			["One"] = 1,
			["Two"] = 2
		};

	public DateTime DateTimeField = DateTime.UtcNow;
	public DateOnly DateOnlyField = DateOnly.FromDateTime(DateTime.Today);
	public TimeOnly TimeOnlyField = TimeOnly.FromDateTime(DateTime.Now);
	public TimeSpan TimeSpanField = TimeSpan.FromMinutes(5);

	public Guid GuidField = Guid.NewGuid();

	public DayOfWeek EnumField = DayOfWeek.Monday;
	public TestStruct StructField = new(123);
	public TestClass ClassField = new("Bob");
	public TestRecord RecordField = new("Alice", 42);
	public TestRecordStruct RecordStructField = new(7);
	public ITestInterface InterfaceField = new TestClass("Interface");
	public Func<int, int> DelegateField = x => x * 2;
	public Tuple<int, string> TupleField = new(1, "tuple");
	public (int Id, string Name) ValueTupleField = (123, "value tuple");
	public GenericBox<string> GenericField = new("generic");
	public dynamic DynamicField = "dynamic value";
	public static int StaticField = 999;
	public readonly string ReadonlyField = "readonly";
	public const string ConstField = "const";

	public int IntProperty { get; set; } = 100;
	public string? NullableStringProperty { get; set; } = null;
	public TestClass ClassProperty { get; set; } = new("Property");
	public TestRecord RecordProperty { get; init; } = new("InitProperty", 5);
	public int ComputedProperty => IntField * 2;

	public void Test()
	{
		bool localBool = true;
		byte localByte = 1;
		sbyte localSByte = -1;
		short localShort = -2;
		ushort localUShort = 2;
		int localInt = 3;
		uint localUInt = 4;
		long localLong = 5;
		ulong localULong = 6;

		char localChar = 'Z';

		float localFloat = 1.5f;
		double localDouble = 2.5;
		decimal localDecimal = 3.5m;
		decimal? localNullableDecimal = 2.5m;
		decimal? localNullableDecimalNull = null;

		int? localNullableInt = 123;
		int? localNullableIntNull = null;

		string localString = "hello";
		string? localNullableString = null;

		object localObject = new();
		object? localNullableObject = null;
		object localBoxedInt = 42;

		int[] localArray = [1, 2, 3];

		List<string> localList = ["a", "b"];
		Dictionary<int, string> localDictionary =
			new()
			{
				[1] = "one"
			};

		TestStruct localStruct = new(10);
		TestClass localClass = new("local");
		TestRecord localRecord = new("record", 1);
		ITestInterface localInterface = new TestClass("interface");
		Func<int, int> localDelegate = x => x + 1;
		Tuple<int, string> localTuple = new(1, "stringInTuple");
		(int A, string B) localValueTuple = (2, "stringInValueTuple");
		GenericBox<int> localGeneric = new(42);
		dynamic localDynamic = 241;
		var localAnonymous = new { Id = 1, Name = "Anonymous" };

		DateTime localDateTime = new(2026, 6, 13, 5, 42, 39);
		Guid localGuid = new Guid("27de5b68-af24-4e59-a785-dde52e2ea7af");
		;
	}
}

public interface ITestInterface
{
	string Name { get; }
}

public class TestClass : ITestInterface
{
	public string Name { get; }

	public TestClass(string name)
	{
		Name = name;
	}
}

public record TestRecord(string Name, int Age);

public readonly record struct TestRecordStruct(int Value);

public readonly struct TestStruct
{
	public int Value { get; }

	public TestStruct(int value)
	{
		Value = value;
	}
}

public class GenericBox<T>
{
	public T Value { get; }

	public GenericBox(T value)
	{
		Value = value;
	}
}
