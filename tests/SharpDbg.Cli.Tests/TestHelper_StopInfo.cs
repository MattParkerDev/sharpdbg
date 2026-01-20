using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using Newtonsoft.Json.Linq;

namespace SharpDbg.Cli.Tests;

public static partial class TestHelper
{
    private static void FillingMissingNetCoreDbgStopInfo(DebugProtocolHost debugProtocolHost, StoppedEvent stoppedEvent)
    {
        var additionalProperties = stoppedEvent.AdditionalProperties;
        if (additionalProperties.Count is 0)
        {
            // Netcoredbg doesn't provide source and line info in StoppedEvent
            var stackTraceRequest = new StackTraceRequest
                { ThreadId = stoppedEvent.ThreadId!.Value, StartFrame = 0, Levels = 1 };
            var stackTraceResponse = debugProtocolHost.SendRequestSync(stackTraceRequest);
            var topFrame = stackTraceResponse.StackFrames.Single();
            var filePath = topFrame.Source.Path;
            var line = topFrame.Line;
            var source = new Source { Path = filePath };
            additionalProperties["source"] = JToken.FromObject(source);
            additionalProperties["line"] = JToken.FromObject(line);
        }
    }
}