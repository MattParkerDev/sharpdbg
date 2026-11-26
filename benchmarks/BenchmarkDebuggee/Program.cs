// The program the benchmarks debug. Benchmarks set breakpoints and expect stops by line number in this file (see
// DebuggeeSource.cs in SharpDbg.Benchmarks), so moving code here means updating those numbers. The harness checks the
// text of every line it uses before it runs, so a stale number fails loudly instead of measuring the wrong thing.
using System.Runtime.CompilerServices;

// Safety net: if a debugger is killed without taking the debuggee with it, don't leave it spinning forever
using var watchdog = new Timer(_ => Environment.Exit(1), null, TimeSpan.FromMinutes(15), Timeout.InfiniteTimeSpan);

switch (args.Length > 0 ? args[0] : "")
{
	case "launch": Scenarios.Launch(); break;
	case "step": Scenarios.StepLoop(); break;
	case "inspect": Scenarios.Inspect(); break;
	case "conditional": Scenarios.Conditional(); break;
	default: throw new ArgumentException($"Unknown scenario '{(args.Length > 0 ? args[0] : "")}'");
}

static class Scenarios
{
	public static int Sink;

	public static void Launch()
	{
		Sink = 1; // launch breakpoint
		while (true) Thread.Sleep(100);
	}

	public static void StepLoop()
	{
		var counter = 0;
		while (true)
		{
			counter = Add(counter, 1); // step start
			counter = Add(counter, 2); // step over lands here
			if (counter > 1_000_000) counter = 0;
		}
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static int Add(int a, int b)
	{
		var sum = a + b; // step out start
		return sum;
	}

	public static void Inspect()
	{
		var wide = new WideObject();
		var numbers = Enumerable.Range(0, 1000).ToList();
		while (true) Recurse(20, wide, numbers);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static void Recurse(int depth, WideObject wide, List<int> numbers)
	{
		if (depth > 0)
		{
			Recurse(depth - 1, wide, numbers);
			return;
		}
		var text = "hello world";
		var total = numbers.Count + wide.Field07;
		Sink = total + text.Length; // inspect breakpoint
	}

	public static void Conditional()
	{
		while (true)
		{
			for (var i = 0; i < 1000; i++)
			{
				Sink = i; // conditional breakpoint
			}
		}
	}
}

class WideObject
{
	public int Field00 = 0, Field01 = 1, Field02 = 2, Field03 = 3, Field04 = 4, Field05 = 5, Field06 = 6, Field07 = 7, Field08 = 8, Field09 = 9;
	public int Field10 = 10, Field11 = 11, Field12 = 12, Field13 = 13, Field14 = 14, Field15 = 15, Field16 = 16, Field17 = 17, Field18 = 18, Field19 = 19;
	public string Name { get; } = "wide object";
	public string Description { get; } = "an object with many members";
	public DateTime Created { get; } = new(2026, 1, 1);
	public List<string> Tags { get; } = ["a", "b", "c"];
	public Dictionary<string, int> Lookup { get; } = new() { ["one"] = 1, ["two"] = 2 };
	public WideObject? Child => null;
}
