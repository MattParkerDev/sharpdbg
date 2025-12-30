using System.IO.Pipes;
using SharpDbg.Application;

namespace SharpDbg.Cli.Tests.Helpers;

public static class InMemoryDebugAdapterHelper
{
	public static (AnonymousPipeServerStream input, AnonymousPipeClientStream output, DebugAdapter debugAdapter) GetAdapterStreams(ITestOutputHelper testOutputHelper)
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
			adapter.Protocol.WaitForReader();
			stdInServer.Dispose();
			stdInClient.Dispose();
			stdOutServer.Dispose();
			stdOutClient.Dispose();
		});

		return (stdInServer, stdOutClient, adapter);

		void Log(string message)
		{
			testOutputHelper.WriteLine($"Log [DotnetDbg]: {message}");
		}
	}
}
