namespace DebuggableConsoleApp;

// A worker thread counts while the test thread runs, so a single-thread continue or step can be seen to leave it frozen
public static class SingleThreadExecutionClass
{
	private static volatile int _workerCounter;
	private static volatile bool _stopWorker;

	public static void Test()
	{
		_stopWorker = false;
		_workerCounter = 0;
		var worker = new Thread(RunWorker) { IsBackground = true, Name = "SingleThreadExecutionWorker" };
		worker.Start();
		WaitForWorkerToCount();
		var first = 1;
		var second = first + 1;
		var third = second + 1;
		Increment(third);
		WaitForWorkerToCount();
		_stopWorker = true;
		worker.Join();
	}

	private static int Increment(int value)
	{
		return value + 1;
	}

	// Returns only once the worker has counted, so never while it is suspended
	private static void WaitForWorkerToCount()
	{
		var count = _workerCounter;
		while (_workerCounter == count) Thread.Yield();
	}

	private static void RunWorker()
	{
		// Only this thread writes the count, so the increment need not be atomic
		while (_stopWorker is false) _workerCounter++;
	}

	// The timer that ends the delay, and the thread the method resumes on, are not the stepping thread
	public static async Task TestAwaitAsync()
	{
		await Task.Delay(50);
		var afterDelay = 1;
		var afterThat = afterDelay + 1;
	}

	public static void TestThreadExit()
	{
		var worker = new Thread(RunShortWorker) { IsBackground = true, Name = "SingleThreadExecutionShortWorker" };
		worker.Start();
		worker.Join();
		var joined = 1;
	}

	private static void RunShortWorker()
	{
		var ran = 1;
	}
}
