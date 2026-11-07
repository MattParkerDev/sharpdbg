namespace DebuggableConsoleApp;

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

	// AsyncValueTaskMethodBuilder has no SetNotificationForWaitCompletion of its own
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

	// Left before its first await, while AsyncVoidMethodBuilder.Start is on the stack, and after it, once the thread pool resumed it
	private static async void FireAndForget()
	{
		var pending = 1;
		await Task.Delay(10);
		Console.WriteLine(pending);
	}
}
