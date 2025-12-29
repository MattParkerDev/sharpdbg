namespace DebuggableConsoleApp;

public class MyClassNoMembers
{
	public void MyMethod(long myParam)
	{
		var myInt = 4;
		var anotherVar = "asdf";
	}
}

public class MyClassWithGeneric<T>
{
	public T[] GenericItems { get; set; }
}
