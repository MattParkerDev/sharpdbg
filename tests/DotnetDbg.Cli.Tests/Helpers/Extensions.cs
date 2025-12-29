namespace DotnetDbg.Cli.Tests.Helpers;

public static class Extensions
{
	extension(Task task)
	{
		public static void RunWithTimeout(Action action, Action onTimeout, TimeSpan? timeout = null)
		{
			var t = Task.Run(action);
			var limit = timeout ?? TimeSpan.FromSeconds(5);

			if (!t.Wait(limit))
			{
				onTimeout?.Invoke();
				throw new TimeoutException($"Operation did not complete within {limit.TotalSeconds} seconds.");
			}
		}
	}
}
