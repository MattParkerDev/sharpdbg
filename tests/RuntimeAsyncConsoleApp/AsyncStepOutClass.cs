namespace RuntimeAsyncConsoleApp;

// Async step-outs whose destination must not depend on Just My Code
public static class AsyncStepOutClass
{
	public static async Task TestAsync()
	{
		var fromValueTask = await ValueTaskAsync();
		var fromTask = await TaskAsync();
		FireAndForget();
		Console.WriteLine(fromValueTask + fromTask);
	}

	// ValueTask must have the same step-out destination as Task, even without a compiler-generated builder
	private static async ValueTask<int> ValueTaskAsync()
	{
		await Task.Delay(10);
		return 2;
	}

	private static async Task<int> TaskAsync()
	{
		await Task.Delay(10);
		return 3;
	}

	// async void still uses a state machine: after suspension there is no awaiting caller to step out to
	private static async void FireAndForget()
	{
		var pending = 1;
		await Task.Delay(10);
		Console.WriteLine(pending);
	}
}
