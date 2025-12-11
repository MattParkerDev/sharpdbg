using DotnetDbg.Application;

namespace DotnetDbg.Cli.Tests;

public static class InMemoryDebugAdapterHelper
{
	public static (Stream input, Stream output) GetAdapterStreams(ITestOutputHelper testOutputHelper)
	{
		var input = new MemoryStream();
		var output = new MemoryStream();
		var adapter = new DebugAdapter(Log);
		adapter.Initialize(input, output);
		adapter.Protocol.Run();
		// WaitForReader() blocks until the input stream is closed (client disconnects)
		_ = Task.Run(() =>
		{
			//adapter.Protocol.WaitForReader();
		});
		return (input, output);
		void Log(string message)
		{
			testOutputHelper.WriteLine(message);
		}
	}
}
