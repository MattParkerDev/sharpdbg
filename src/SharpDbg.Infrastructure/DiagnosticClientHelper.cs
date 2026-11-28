using System.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;

namespace SharpDbg.Infrastructure;

public static class DiagnosticClientHelper
{
	// For applications like godot, which start their own CLR, diagnostics IPC may not be available immediately.
	// Retry until it succeeds, waiting only once an attempt has failed.
	public static async Task DiagnosticClientResumeRuntime(int debuggeeProcessId)
	{
		await WaitUntilDiagnosticPipeIsListening(debuggeeProcessId);
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

	private static readonly TimeSpan DiagnosticPipeWaitTimeout = TimeSpan.FromSeconds(10);
	private static readonly TimeSpan DiagnosticPipeListenGrace = TimeSpan.FromMilliseconds(50);

	// Remove once https://github.com/dotnet/runtime/pull/138447 is merged and released
	private static async Task WaitUntilDiagnosticPipeIsListening(int processId)
	{
		if (OperatingSystem.IsWindows() is false) return;
		var pipeName = $"dotnet-diagnostic-{processId}";
		var waited = Stopwatch.StartNew();
		while (Directory.GetFiles(@"\\.\pipe\", pipeName).Length is 0)
		{
			// Let the resume report why it cannot connect
			if (waited.Elapsed > DiagnosticPipeWaitTimeout) return;
			await Task.Delay(5);
		}
		await Task.Delay(DiagnosticPipeListenGrace);
	}
}
