namespace DebuggableConsoleApp;

public class ClassWithIndexer
{
	public int Value = 1;
	public int this[int index] => index;

	public static void Test()
	{
		var classWithIndexer = new ClassWithIndexer();
		Console.WriteLine(classWithIndexer);
	}
}
