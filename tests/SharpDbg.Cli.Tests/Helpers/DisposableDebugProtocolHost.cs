using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace SharpDbg.Cli.Tests.Helpers;

public class DisposableDebugProtocolHost(Stream debugAdapterStdIn, Stream debugAdapterStdOut, bool registerStandardHandlers, bool terminateDebuggeeOnDispose = false) : DebugProtocolHost(debugAdapterStdIn, debugAdapterStdOut, registerStandardHandlers), IDisposable
{
	public void Dispose()
	{
		GC.SuppressFinalize(this);
		SendRequestSync(new DisconnectRequest { TerminateDebuggee = terminateDebuggeeOnDispose });
		Stop();
		WaitForReader();
	}
}
