namespace RuntimeAsyncConsoleApp;

// Awaiting the task separately gives WorkAsync its own continuation chain, so a step out must find the caller through the task
public static class AsyncUnawaitedTaskClass
{
	public static async Task TestAsync()
	{
		var task = WorkAsync();
		Console.WriteLine("between");
		var result = await task;
		Console.WriteLine(result);
	}

	private static async Task<int> WorkAsync()
	{
		var seed = 1;
		await Task.Delay(10);
		var next = seed + 1;
		return next;
	}
}
