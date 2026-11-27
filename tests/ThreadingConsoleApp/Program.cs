using System.Threading;

namespace ThreadingConsoleApp;

public static class Program
{
	private static readonly ManualResetEventSlim Gate = new(false);
	private static volatile bool _backgroundRan;

	public static void Main()
	{
		var background = new Thread(() =>
		{
			Gate.Wait();
			_backgroundRan = true;
		}) { IsBackground = true };
		background.Start();

		// Let the background thread reach Gate.Wait before the breakpoint is hit.
		Thread.Sleep(200);

		Gate.Set();
		var ran = SpinWait.SpinUntil(() => _backgroundRan, 1000);
		if (!ran)
		{
			Console.WriteLine("background did not run");
		}
		else
		{
			Console.WriteLine("background ran");
		}
	}
}
