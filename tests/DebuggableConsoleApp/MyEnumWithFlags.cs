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
