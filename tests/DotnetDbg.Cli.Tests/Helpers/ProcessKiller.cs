using System.Diagnostics;

namespace DotnetDbg.Cli.Tests.Helpers;

public class ProcessKiller(Process process) : IDisposable
{
	public void Dispose()
	{
		if (process.HasExited is false)
		{
			try
			{
				process.Kill(entireProcessTree: true);
				process.Dispose();
			}
			catch (Exception)
			{
				// Ignore exceptions during process kill
			}
		}
	}
}
