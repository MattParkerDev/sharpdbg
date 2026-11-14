namespace DebuggableConsoleApp;

// A step out of an awaited method stops on the await's line, as a step out of a synchronous call stops on the call's line, so the rest
// of the statement, here the call the result is passed to, can still be stepped into
public static class AsyncNestedAwaitClass
{
	public static async Task TestAsync()
	{
		Process(await DoneAtOnceAsync());
		Process(await SuspendsAsync());
		Console.WriteLine("done");
	}

	private static void Process(int value)
	{
		Console.WriteLine(value);
	}

	private static async Task<int> DoneAtOnceAsync()
	{
		var value = 1;
		await Task.CompletedTask;
		return value;
	}

	private static async Task<int> SuspendsAsync()
	{
		var value = 2;
		await Task.Delay(10);
		return value;
	}
}
