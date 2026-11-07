using System.Collections.Concurrent;
using System.Threading.Channels;
using SharpDbg.DapProtocol;

namespace SharpDbg.Cli.Tests.Helpers;

/// <summary>
/// A DAP client for driving the debug adapter from tests.
/// </summary>
public sealed class DebugProtocolHost : IDisposable
{
	private static readonly TimeSpan DefaultEventTimeout = TimeSpan.FromSeconds(30);

	private readonly DapConnection _connection;
	private readonly bool _terminateDebuggeeOnDispose;
	// Every event, in order. Waits consume from the queue, the received events are kept for assertions and timeout messages
	private readonly Channel<IDapEvent> _eventQueue = Channel.CreateUnbounded<IDapEvent>(new UnboundedChannelOptions { SingleReader = true });
	private readonly ConcurrentQueue<IDapEvent> _receivedEvents = new();

	public DebugProtocolHost(Stream debugAdapterStdIn, Stream debugAdapterStdOut, bool terminateDebuggeeOnDispose = false, Action<string>? log = null)
	{
		_connection = new DapConnection(debugAdapterStdOut, debugAdapterStdIn, handler: null, new DapConnectionOptions { Log = log });
		_terminateDebuggeeOnDispose = terminateDebuggeeOnDispose;

		RecordEvents<InitializedEvent>();
		RecordEvents<StoppedEvent>();
		RecordEvents<ContinuedEvent>();
		RecordEvents<ExitedEvent>();
		RecordEvents<TerminatedEvent>();
		RecordEvents<ThreadEvent>();
		RecordEvents<OutputEvent>();
		RecordEvents<BreakpointEvent>();
		RecordEvents<ModuleEvent>();
		RecordEvents<LoadedSourceEvent>();
		RecordEvents<ProcessEvent>();
		RecordEvents<CapabilitiesEvent>();
		RecordEvents<ProgressStartEvent>();
		RecordEvents<ProgressUpdateEvent>();
		RecordEvents<ProgressEndEvent>();
		RecordEvents<InvalidatedEvent>();
		RecordEvents<MemoryEvent>();
	}

	public DapConnection Connection => _connection;

	public IReadOnlyCollection<IDapEvent> ReceivedEvents => _receivedEvents;

	public void Run() => _connection.Start();

	/// <summary>
	/// Takes events off the queue in order until one of type <typeparamref name="T"/> matches <paramref name="condition"/>, discarding the rest.
	/// A stop is never discarded, as a later wait would then miss it - skipping one fails instead.
	/// </summary>
	public async Task<T> WaitForEvent<T>(Predicate<T>? condition = null, TimeSpan? timeout = null) where T : IDapEvent
	{
		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		timeoutCts.CancelAfter(timeout ?? DefaultEventTimeout);
		while (true)
		{
			IDapEvent dapEvent;
			try
			{
				dapEvent = await _eventQueue.Reader.ReadAsync(timeoutCts.Token);
			}
			catch (OperationCanceledException) when (TestContext.Current.CancellationToken.IsCancellationRequested is false)
			{
				throw new TimeoutException($"Timed out waiting for {typeof(T).Name}. Events received so far: {DescribeReceivedEvents()}");
			}

			if (dapEvent is T match && condition?.Invoke(match) is not false) return match;
			if (dapEvent is StoppedEvent stoppedEvent)
			{
				throw new InvalidOperationException($"Waiting for {typeof(T).Name}, but the debuggee stopped first (reason '{stoppedEvent.Reason}'). Events received so far: {DescribeReceivedEvents()}");
			}
		}
	}

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
		_connection.SendRequest(new DisconnectArguments { TerminateDebuggee = _terminateDebuggeeOnDispose });
		_connection.Stop();
	}

	private void RecordEvents<T>() where T : IDapEvent, IDapNamedMessage => _connection.OnEvent<T>(dapEvent =>
	{
		_receivedEvents.Enqueue(dapEvent);
		_eventQueue.Writer.TryWrite(dapEvent);
	});

	// Consecutive duplicates are collapsed ('OutputEvent x12'), so the shape of the stream stays readable
	private string DescribeReceivedEvents()
	{
		var parts = new List<string>();
		string? current = null;
		var count = 0;
		foreach (var dapEvent in _receivedEvents)
		{
			var name = dapEvent is StoppedEvent stoppedEvent ? $"StoppedEvent({stoppedEvent.Reason})" : dapEvent.GetType().Name;
			if (name == current)
			{
				count++;
				continue;
			}
			if (current is not null) parts.Add(count > 1 ? $"{current} x{count}" : current);
			current = name;
			count = 1;
		}
		if (current is not null) parts.Add(count > 1 ? $"{current} x{count}" : current);
		return parts.Count is 0 ? "none" : string.Join(", ", parts);
	}
}

public sealed class DapRequestFailedException(string command, DapError error) : Exception($"Request '{command}' failed: {error.Message}")
{
	public DapError Error { get; } = error;
}
