using System.Diagnostics;
using Ardalis.GuardClauses;
using SharpDbg.Application;

namespace SharpDbg.Cli.Tests.Helpers;

public class ProcessKiller(Process process) : IDisposable
{
	public void Dispose()
	{
		if (process.HasExited is false)
		{
			try
			{
				process.Kill(entireProcessTree: true);
			}
			catch (Exception)
			{
				// Ignore exceptions during process kill
			}
		}
		process.Dispose();
	}
}

public class OopOrInProcDebugAdapter : IDisposable
{
	private OopOrInProcDebugAdapter() { }

	private readonly Process? _process;
	private readonly DebugAdapter? _debugAdapter;
	public OopOrInProcDebugAdapter(Process process)
	{
		Guard.Against.Null(process);
		_process = process;
	}
	public OopOrInProcDebugAdapter(DebugAdapter debugAdapter)
	{
		Guard.Against.Null(debugAdapter);
		_debugAdapter = debugAdapter;
	}

	public void Dispose()
	{
		_debugAdapter?.Protocol.Stop();
		if (_process is not null)
		{
			if (_process.HasExited is false)
			{
				try
				{
					_process.Kill(entireProcessTree: true);
				}
				catch (Exception)
				{
					// Ignore exceptions during process kill
				}
			}
			_process.Dispose();
		}
	}
}
