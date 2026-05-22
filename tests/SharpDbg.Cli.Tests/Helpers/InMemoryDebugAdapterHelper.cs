using System.IO.Pipes;
using SharpDbg.Application;

namespace SharpDbg.Cli.Tests.Helpers;

public static class InMemoryDebugAdapterHelper
{
	public static (AnonymousPipeServerStream input, AnonymousPipeClientStream output, IDisposable debugAdapterDisposable) GetAdapterStreams(ITestOutputHelper testOutputHelper)
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

		var disposable = new InMemoryDebugAdapterDisposable(stdInServer, stdOutServer, stdInClient, stdOutClient, adapter);
		return (stdInServer, stdOutClient, disposable);

		void Log(string message)
		{
			testOutputHelper.WriteLine($"Log [SharpDbg]: {message}");
		}
	}
}

public class InMemoryDebugAdapterDisposable(
	AnonymousPipeServerStream stdInServer,
	AnonymousPipeServerStream stdOutServer,
	AnonymousPipeClientStream stdInClient,
	AnonymousPipeClientStream stdOutClient,
	DebugAdapter debugAdapter)
	: IDisposable
{
	private readonly AnonymousPipeServerStream _stdInServer = stdInServer;
	private readonly AnonymousPipeServerStream _stdOutServer = stdOutServer;
	private readonly AnonymousPipeClientStream _stdInClient = stdInClient;
	private readonly AnonymousPipeClientStream _stdOutClient = stdOutClient;

	private readonly DebugAdapter _debugAdapter = debugAdapter;

	public void Dispose()
	{
		GC.SuppressFinalize(this);
		_debugAdapter.Protocol.Stop();
		_debugAdapter.Protocol.WaitForReader();
		_stdInServer.Dispose();
		_stdOutServer.Dispose();
		_stdInClient.Dispose();
		_stdOutClient.Dispose();
	}
}
