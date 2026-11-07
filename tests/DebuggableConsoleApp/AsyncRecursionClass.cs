namespace DebuggableConsoleApp;

// A recursive async method runs its inner activations synchronously up to their first await, on the same thread and deeper in the
// stack: they reach the same await, in the same MoveNext, before the outer activation does
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
