
namespace DebuggableConsoleApp;

public static class Program
{
	public static void Main(string[] args)
	{
		Console.WriteLine("DebuggableConsoleApp is running");
		Console.WriteLine("Log2");
		while (true)
		{
			// Keep the application running to allow debugging
			System.Threading.Thread.Sleep(1000);
		}
	}
}
