using System.Diagnostics;

namespace SharpDbg.Cli.Tests.Helpers;

// A launched debuggee outlives its session - Dispose releases the Process object without killing it, and a
// disconnect asking for termination does not return. Only processes that appeared afterwards are touched.
public sealed class LaunchedDebuggeeKiller : IDisposable
{
	// An apphost runs under its own name, and a managed assembly runs under the muxer's
	private static readonly string[] CandidateNames = ["DebuggableConsoleApp", "dotnet"];

	private readonly HashSet<int> _preExistingProcessIds = GetCandidateProcessIds();

	public void Dispose()
	{
		foreach (var processId in GetCandidateProcessIds().Except(_preExistingProcessIds))
		{
			try
			{
				using var process = Process.GetProcessById(processId);
				process.Kill(entireProcessTree: true);
			}
			catch
			{
				// It already exited, or it is not ours to kill
			}
		}
	}

	private static HashSet<int> GetCandidateProcessIds()
	{
		var processIds = new HashSet<int>();
		foreach (var name in CandidateNames)
		{
			foreach (var process in Process.GetProcessesByName(name))
			{
				using (process) processIds.Add(process.Id);
			}
		}
		return processIds;
	}
}
