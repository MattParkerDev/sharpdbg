namespace RuntimeAsyncConsoleApp;

// Recursive calls share the method's resume breakpoint and continuation chain, so stepping must also distinguish their depth
// Inner calls resume first; the step must wait for the activation it started in
public static class AsyncRecursionClass
{
	public static async Task TestAsync()
	{
		var result = await Walk(3);
		Console.WriteLine(result);
	}

	private static async Task<int> Walk(int n)
	{
		if (n == 0) return 0;
		var inner = await Walk(n - 1);
		await Task.Delay(10);
		return inner + n;
	}
}
