namespace DebuggableConsoleApp;

[Flags]
public enum MyEnumWithFlags
{
	None = 0,
	FlagValue1 = 1,
	FlagValue2 = 2,
	FlagValue3 = 4,
}

public struct MyStruct
{
	public int Id;
	public string Name;

	public static MyStruct operator +(MyStruct left, MyStruct right) => new() { Id = left.Id + right.Id, Name = left.Name };
	public static MyStruct operator +(int left, MyStruct right) => new() { Id = left + right.Id, Name = right.Name };
	public static MyStruct operator -(MyStruct value) => new() { Id = -value.Id, Name = value.Name };

	public static bool operator ==(MyStruct left, MyStruct right) => left.Id == right.Id && left.Name == right.Name;
	public static bool operator !=(MyStruct left, MyStruct right) => !(left == right);
	public override bool Equals(object? obj) => obj is MyStruct other && this == other;
	public override int GetHashCode() => HashCode.Combine(Id, Name);
	public int GetId() => Id;
}

public static class MyStructExtensions
{
	public static int ExtensionMethod(this MyStruct myStruct) => myStruct.Id;
}

public static class MyStructExtensions2
{
	extension(MyStruct myStruct)
	{
		public int NewExtensionMethod() => myStruct.Id;
		public int ExtensionProperty => myStruct.Id;
	}
}
