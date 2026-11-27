using Microsoft.Diagnostics.NETCore.Client;

namespace SharpDbg.Infrastructure;

public static class DiagnosticClientHelper
{
	// For applications like godot, which start their own CLR, diagnostics IPC may not be available immediately.
	// Retry until it succeeds, waiting only once an attempt has failed.
	public static async Task DiagnosticClientResumeRuntime(int debuggeeProcessId)
	{
		var diagnosticsClient = new DiagnosticsClient(debuggeeProcessId);
		const int maxRetries = 5;
		var delayMs = 50;

		for (var attempt = 1; attempt <= maxRetries; attempt++)
		{
			try
			{
				diagnosticsClient.ResumeRuntime();
				return;
			}
			catch (ServerNotAvailableException) when (attempt < maxRetries)
			{
				await Task.Delay(delayMs);
				delayMs *= 2;
			}
		}
		diagnosticsClient.ResumeRuntime();
	}
}
