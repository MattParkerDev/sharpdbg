using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using Newtonsoft.Json.Linq;

namespace SharpDbg.Cli.Tests.Helpers;

public static class StoppedEventHelper
{
	public static (string filePath, int line) ReadStopInfo(this StoppedEvent stoppedEvent)
	{
		var additionalProperties = stoppedEvent.AdditionalProperties;
		if (additionalProperties.Count is 0) throw new InvalidOperationException("StoppedEvent has no AdditionalProperties");
		var filePath = additionalProperties?["source"]?["path"]!.Value<string>()!;
		var line = (additionalProperties?["line"]?.Value<int>()!).Value;
		return (filePath, line);
	}
}
