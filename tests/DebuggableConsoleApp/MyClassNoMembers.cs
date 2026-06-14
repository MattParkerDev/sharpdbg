namespace DebuggableConsoleApp;

public class MyClassNoMembers
{
	public void MyMethod(long myParam)
	{
		var myInt = 4;
		var anotherVar = "asdf";
	}

	public string Test(int test)
	{
		return "stringValue2";
	}
}

public class MyClassWithGeneric<T>
{
	public required T[] GenericItemsField;
	public required T[] GenericItems { get; set; }
}
