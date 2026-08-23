namespace DebuggableConsoleApp;

public static class AsyncStackTraceClass
{
	public static async Task TestAsync()
	{
		await MiddleAsync();
	}

	private static async Task MiddleAsync()
	{
		var middleValue = 17;
		await InnerAsync();
		GC.KeepAlive(middleValue);
	}

	private static async Task InnerAsync()
	{
		await Task.Delay(10);
		;
	}
}
