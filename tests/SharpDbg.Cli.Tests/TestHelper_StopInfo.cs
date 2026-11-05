using System.Text.Json.Nodes;
using SharpDbg.Cli.Tests.Helpers;
using SharpDbg.DapProtocol;

namespace SharpDbg.Cli.Tests;

public static partial class TestHelper
{
	private static void FillingMissingNetCoreDbgStopInfo(DebugProtocolHost debugProtocolHost, StoppedEvent stoppedEvent)
	{
		if (stoppedEvent.AdditionalProperties is null || stoppedEvent.AdditionalProperties.Count is 0)
		{
			// Netcoredbg doesn't provide source and line info in StoppedEvent
			var stackTraceArguments = new StackTraceArguments { ThreadId = stoppedEvent.ThreadId!.Value, StartFrame = 0, Levels = 1 };
			var stackTraceResponse = debugProtocolHost.SendRequestSync(stackTraceArguments);
			var topFrame = stackTraceResponse.StackFrames.Single();
			if (topFrame.Source is null) return; // Stop has no source, e.g. exception in library code with JMC Enabled
			var filePath = topFrame.Source.Path!.Replace('/', Path.DirectorySeparatorChar);
			stoppedEvent.AdditionalProperties = new JsonObject
			{
				["source"] = new JsonObject { ["path"] = filePath },
				["line"] = topFrame.Line,
				["column"] = topFrame.Column
			};
		}
	}
}
