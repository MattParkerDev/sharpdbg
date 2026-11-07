namespace DebuggableConsoleApp;

// An async method whose task is kept in a variable and only awaited later
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
