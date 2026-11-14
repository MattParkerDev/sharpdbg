namespace DebuggableConsoleApp;

// Process must remain available to step into after stepping out of either awaited method
// This checks the same destination with and without suspension
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
