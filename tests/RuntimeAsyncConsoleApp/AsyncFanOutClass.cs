namespace RuntimeAsyncConsoleApp;

// The shorter second delay makes the other activation hit the shared resume breakpoint before the stepping activation
// Awaiting both tasks separately also tests calls with independent continuation chains
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
