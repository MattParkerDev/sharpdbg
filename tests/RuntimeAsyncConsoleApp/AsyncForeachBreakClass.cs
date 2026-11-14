namespace RuntimeAsyncConsoleApp;

// A 'break' out of an 'await foreach' jumps over the loop's MoveNextAsync await to the hidden DisposeAsync, which really yields here
public static class AsyncForeachBreakClass
{
	public static async Task TestAsync()
	{
		await foreach (var item in GuardedItemsAsync())
		{
			Console.WriteLine(item);
			break;
		}
		Console.WriteLine("after break");
	}

	// The await in the finally makes the enumerator's DisposeAsync yield when the loop breaks
	private static async IAsyncEnumerable<int> GuardedItemsAsync()
	{
		try
		{
			await Task.Delay(10);
			yield return 1;
			yield return 2;
		}
		finally
		{
			await Task.Delay(10);
		}
	}
}
