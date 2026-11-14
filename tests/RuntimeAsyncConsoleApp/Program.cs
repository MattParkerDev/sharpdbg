namespace RuntimeAsyncConsoleApp;

// Shared debuggee methods keep the same line numbers as DebuggableConsoleApp so both suites can check the same stepping locations
// Runtime async exercises the .NET 11 path; async void methods and async iterators still exercise state-machine stepping
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
			AsyncNestedAwaitClass.TestAsync().GetAwaiter().GetResult();
			AsyncStackTraceClass.TestAsync().GetAwaiter().GetResult();
			AsyncFanOutClass.TestAsync().GetAwaiter().GetResult();
			AsyncMultipleAwaitsClass.TestAsync().GetAwaiter().GetResult();
			Thread.Sleep(100);
		}
	}
}
