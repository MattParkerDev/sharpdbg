using System.Text.Json.Nodes;
using SharpDbg.DapProtocol;

namespace SharpDbg.Cli.Tests.Helpers;

public static class StoppedEventHelper
{
	public static (string filePath, int line, int column) ReadStopInfo(this StoppedEvent stoppedEvent)
	{
		var additionalProperties = stoppedEvent.AdditionalProperties;
		if (additionalProperties is null || additionalProperties.Count is 0) throw new InvalidOperationException("StoppedEvent has no AdditionalProperties");
		var filePath = additionalProperties["source"]!["path"]!.GetValue<string>();
		var line = additionalProperties["line"]!.GetValue<int>();
		var column = additionalProperties["column"]!.GetValue<int>();
		return (filePath, line, column);
	}
}
