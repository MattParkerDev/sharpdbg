
namespace DebuggableConsoleApp;

public static class Program
{
	public static void Main(string[] args)
	{
		Console.WriteLine("DebuggableConsoleApp is running");
		Console.WriteLine("Log2");
		var myClass = new MyClass();
		var myClassNoMembers = new MyClassNoMembers();
		while (true)
		{
			// Keep the application running to allow debugging
			myClass.MyMethod(13);
			myClassNoMembers.MyMethod(42);
			Thread.Sleep(500);
			//await Task.Delay(500);
		}
	}
}
