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
