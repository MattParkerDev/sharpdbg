using System.IO.Pipes;
using System.Text;
using DotnetDbg.Application;

namespace DotnetDbg.Cli.Tests;

public static class InMemoryDebugAdapterHelper
{
	public static (Stream input, Stream output) GetAdapterStreams(ITestOutputHelper testOutputHelper)
	{
		var stdInServer = new AnonymousPipeServerStream(PipeDirection.Out); // write
		var stdInClient = new AnonymousPipeClientStream(PipeDirection.In, stdInServer.ClientSafePipeHandle); // std in read

		var stdOutServer = new AnonymousPipeServerStream(PipeDirection.Out); // write
		var stdOutClient = new AnonymousPipeClientStream(PipeDirection.In, stdOutServer.ClientSafePipeHandle); // std out read

		var adapter = new DebugAdapter(Log);
		adapter.Initialize(stdInClient, stdOutServer);
		adapter.Protocol.VerifySynchronousOperationAllowed();
		adapter.Protocol.Run();
		_ = Task.Run(() =>
		{
			//adapter.Protocol.WaitForReader();
		});

		return (stdInServer, stdOutClient);

		void Log(string message)
		{
			testOutputHelper.WriteLine($"Log [DotnetDbg]: {message}");
		}
	}
}
