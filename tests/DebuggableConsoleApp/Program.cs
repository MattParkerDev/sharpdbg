
namespace DebuggableConsoleApp;

public static class Program
{
	public static void Main(string[] args)
	{
		Console.WriteLine("DebuggableConsoleApp is running");
		Console.WriteLine("Log2");
		var myClass = new MyClass();
		var myAsyncClass = new MyAsyncClass();
		var myClassNoMembers = new MyClassNoMembers();
		while (true)
		{
			// Keep the application running to allow debugging
			myClass.MyMethod(13, 6);
			myClassNoMembers.MyMethod(42);
			var asyncResult = myAsyncClass.MyMethodAsync().GetAwaiter().GetResult();
			Thread.Sleep(100);
			//await Task.Delay(500);
		}
	}
}
