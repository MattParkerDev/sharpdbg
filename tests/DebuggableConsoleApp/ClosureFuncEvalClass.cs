namespace DebuggableConsoleApp;

// Hoisted locals whose display needs a func-eval (List<T>'s [DebuggerDisplay], a ToString() override)
public static class ClosureFuncEvalClass
{
	public static async Task TestAsync()
	{
		var list = new List<int> { 1, 2, 3 };
		var point = new PointWithToString(1, 2);
		await Task.Delay(1);
		Console.WriteLine($"{list.Count} {point}"); // set breakpoint here (async state machine)
		LambdaTest();
	}

	private static int LambdaTest()
	{
		var list = new List<int> { 1, 2, 3 };
		var point = new PointWithToString(1, 2);
		Func<int> f = () =>
		{
			var innerList = new List<int> { 4 };
			Func<int> g = () =>
			{
				return innerList.Count + list.Count + point.X; // set breakpoint here (nested closure, linked to its parent)
			};
			return g(); // set breakpoint here (lambda closure as 'this', nested closure as a local)
		};
		return f(); // set breakpoint here (closure as a local)
	}

	private sealed class PointWithToString(int x, int y)
	{
		public int X { get; } = x;
		public int Y { get; } = y;
		public override string ToString() => $"({X}, {Y})";
	}
}
