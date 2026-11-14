namespace DebuggableConsoleApp;

// Two runs of the same method at once, neither awaited where it is called: the first to reach the await suspends longest, so the second
// goes on after the await first
public static class AsyncFanOutClass
{
	public static async Task TestAsync()
	{
		await Task.WhenAll(WorkAsync(1, 300), WorkAsync(2, 10));
	}

	private static async Task WorkAsync(int id, int delay)
	{
		Console.WriteLine(id);
		await Task.Delay(delay);
		Console.WriteLine(id);
	}
}
