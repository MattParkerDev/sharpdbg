using SharpDbg.DapProtocol;

namespace SharpDbg.Cli.Tests.Helpers;

/// <summary>
/// A DAP client for driving the debug adapter from tests.
/// </summary>
public sealed class DebugProtocolHost(Stream debugAdapterStdIn, Stream debugAdapterStdOut, bool terminateDebuggeeOnDispose = false, Action<string>? log = null) : IDisposable
{
	private readonly DapConnection _connection = new(debugAdapterStdOut, debugAdapterStdIn, handler: null, new DapConnectionOptions { Log = log });

	public DapConnection Connection => _connection;

	public void RegisterEventType<T>(Action<T> handler) where T : IDapEvent, IDapNamedMessage => _connection.OnEvent(handler);

	public void Run() => _connection.Start();

	public DapResult<TResponse> SendRequest<TResponse>(IDapRequest<TResponse> request) => _connection.SendRequest(request);

	/// <summary>Sends a request, throwing <see cref="DapRequestFailedException"/> if the adapter responds with an error.</summary>
	public TResponse SendRequestSync<TResponse>(IDapRequest<TResponse> request) => _connection.SendRequest(request) switch
	{
		DapError error => throw new DapRequestFailedException(request.Command, error),
		TResponse response => response,
		null => throw new InvalidOperationException("No result"),
	};

	public void Dispose()
	{
		_connection.SendRequest(new DisconnectArguments { TerminateDebuggee = terminateDebuggeeOnDispose });
		_connection.Stop();
	}
}

public sealed class DapRequestFailedException(string command, DapError error) : Exception($"Request '{command}' failed: {error.Message}")
{
	public DapError Error { get; } = error;
}
