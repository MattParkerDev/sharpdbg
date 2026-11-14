namespace RuntimeAsyncConsoleApp;

// The async classes of DebuggableConsoleApp, line for line, built for .NET 11, where async methods are compiled with runtime async:
// no state machine, the runtime suspends and resumes the method itself (async void methods and async iterators keep their state machines)
public static class Program
{
	public static void Main(string[] args)
	{
		Console.WriteLine("RuntimeAsyncConsoleApp is running");
		var myAsyncClass = new MyAsyncClass();
		var myAsyncMethodEvalClass = new AsyncMethodEvalClass();
		while (true)
		{
			var asyncResult = myAsyncClass.MyMethodAsync(4).GetAwaiter().GetResult();
			myAsyncMethodEvalClass.Test().GetAwaiter().GetResult();
			AsyncStepOutClass.TestAsync().GetAwaiter().GetResult();
			AsyncUnawaitedTaskClass.TestAsync().GetAwaiter().GetResult();
			AsyncForeachBreakClass.TestAsync().GetAwaiter().GetResult();
			AsyncRecursionClass.TestAsync().GetAwaiter().GetResult();
			Thread.Sleep(100);
		}
	}
}
