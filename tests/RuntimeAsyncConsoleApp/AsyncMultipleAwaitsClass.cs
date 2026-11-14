namespace RuntimeAsyncConsoleApp;

// Statements with more than one await: a step over goes on to the next line whichever of them suspend
public static class AsyncMultipleAwaitsClass
{
	public static async Task TestAsync()
	{
		Consume(await SuspendsAsync(1), await SuspendsAsync(2));
		Consume(await DoneAtOnceAsync(3), await SuspendsAsync(4));
		Console.WriteLine("done");
	}

	private static void Consume(int first, int second)
	{
		Console.WriteLine(first + second);
	}

	private static async Task<int> SuspendsAsync(int value)
	{
		await Task.Delay(10);
		return value;
	}

	private static async Task<int> DoneAtOnceAsync(int value)
	{
		await Task.CompletedTask;
		return value;
	}
}
