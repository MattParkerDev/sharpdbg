using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;

namespace SharpDbg.DapProtocol;

/// <summary>
/// Options for a <see cref="DapConnection"/>.
/// </summary>
public sealed class DapConnectionOptions
{
	/// <summary>Serializer options for messages. Defaults to <see cref="DapJson.DefaultOptions"/>.</summary>
	public JsonSerializerOptions? SerializerOptions { get; init; }

	/// <summary>Receives diagnostic messages, e.g. handler failures and malformed messages.</summary>
	public Action<string>? Log { get; init; }
}

/// <summary>
/// A Debug Adapter Protocol connection over a pair of streams. The same type is used by debug adapters and by clients:
/// both sides can send requests and events, and receive requests (via an <see cref="IDapRequestHandler"/>) and events.
/// </summary>
/// <remarks>
/// Incoming messages are read on a dedicated thread. Responses to outgoing requests are completed directly by the reader,
/// so a request handler may synchronously wait for the response to a request it sends (e.g. <c>runInTerminal</c>).
/// Incoming requests and events are dispatched in order on a separate dispatch loop.
/// </remarks>
public sealed class DapConnection : IDisposable
{
	private static ReadOnlySpan<byte> HeaderTerminator => "\r\n\r\n"u8;
	private static ReadOnlySpan<byte> EmptyObject => "{}"u8;

	private readonly Stream _input;
	private readonly Stream _output;
	private readonly IDapRequestHandler? _handler;
	private readonly JsonSerializerOptions _json;
	private readonly Action<string>? _log;

	private readonly Lock _writeLock = new();
	private readonly Lock _stateLock = new();
	private int _seq;

	private readonly ConcurrentDictionary<string, Type> _requestTypes = new(DapMessageTypes.Requests);
	private readonly ConcurrentDictionary<string, Type> _eventTypes = new(DapMessageTypes.Events);
	private readonly ConcurrentDictionary<string, EventSubscription[]> _eventSubscriptions = new();
	private readonly ConcurrentDictionary<int, PendingRequest> _pendingRequests = new();
	private readonly ConcurrentDictionary<int, CancellationTokenSource> _inflightRequests = new();

	private readonly Channel<IncomingMessage> _incoming = Channel.CreateUnbounded<IncomingMessage>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
	private readonly CancellationTokenSource _stopCts = new();
	private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private int _started;
	private int _stopped;

	/// <summary>Creates a connection that reads messages from <paramref name="input"/> and writes messages to <paramref name="output"/>.</summary>
	/// <param name="input">The stream messages are read from.</param>
	/// <param name="output">The stream messages are written to.</param>
	/// <param name="handler">Handles incoming requests. When null, all incoming requests are answered with a "not supported" error.</param>
	/// <param name="options">Connection options.</param>
	public DapConnection(Stream input, Stream output, IDapRequestHandler? handler = null, DapConnectionOptions? options = null)
	{
		_input = input;
		_output = output;
		_handler = handler;
		_json = options?.SerializerOptions ?? DapJson.DefaultOptions;
		_log = options?.Log;
	}

	/// <summary>Completes when the connection stops, either because the input stream ended or <see cref="Stop"/> was called.</summary>
	public Task Completion => _completion.Task;

	/// <summary>Raised when the connection fails in a way that is not attributable to a single request, e.g. a malformed message.</summary>
	public event Action<Exception>? ProtocolError;

	/// <summary>Starts reading and dispatching incoming messages.</summary>
	public void Start()
	{
		if (Interlocked.Exchange(ref _started, 1) is not 0) throw new InvalidOperationException("The connection has already been started.");
		var readerThread = new System.Threading.Thread(ReadLoop) { IsBackground = true, Name = "DAP reader" };
		readerThread.Start();
		_ = Task.Run(DispatchLoopAsync);
	}

	/// <summary>
	/// Stops the connection: pending outgoing requests fail, in-flight incoming requests are cancelled and queued messages are discarded.
	/// Requests already admitted by the dispatch loop may still be running; their handlers are not awaited.
	/// The streams are not disposed.
	/// </summary>
	public void Stop()
	{
		lock (_stateLock)
		{
			if (_stopped is not 0) return;
			Volatile.Write(ref _stopped, 1);
		}
		_incoming.Writer.TryComplete();
		foreach (var seq in _pendingRequests.Keys)
		{
			if (_pendingRequests.TryRemove(seq, out var pending)) pending.Fail(new DapError("The connection was closed."));
		}
		try
		{
			_stopCts.Cancel();
		}
		finally
		{
			_completion.TrySetResult();
		}
	}

	/// <inheritdoc cref="Stop"/>
	public void Dispose() => Stop();

	/// <summary>Registers a custom request type, so incoming requests with its command are deserialized as <typeparamref name="T"/>.</summary>
	public void RegisterRequest<T>() where T : IDapRequest, IDapNamedMessage => _requestTypes[T.MessageName] = typeof(T);

	/// <summary>Invokes <paramref name="handler"/> on the dispatch loop whenever an event of type <typeparamref name="T"/> is received.</summary>
	/// <returns>A subscription which removes the handler when disposed.</returns>
	public IDisposable OnEvent<T>(Action<T> handler) where T : IDapEvent, IDapNamedMessage
	{
		var name = T.MessageName;
		_eventTypes[name] = typeof(T);
		var subscription = new EventSubscription(this, name, e => handler((T)e));
		_eventSubscriptions.AddOrUpdate(name, _ => [subscription], (_, existing) => [.. existing, subscription]);
		return subscription;
	}

	/// <summary>Sends an event to the peer.</summary>
	public void SendEvent<T>(T @event) where T : IDapEvent
	{
		var typeInfo = _json.GetTypeInfo(@event.GetType());
		WriteMessage(writer =>
		{
			writer.WriteString("type"u8, "event"u8);
			writer.WriteString("event"u8, @event.Event);
			writer.WritePropertyName("body"u8);
			JsonSerializer.Serialize(writer, @event, typeInfo);
		});
	}

	/// <summary>Sends a request to the peer and waits for its response.</summary>
	/// <remarks>Cancelling <paramref name="cancellationToken"/> sends a <c>cancel</c> request to the peer and throws <see cref="OperationCanceledException"/>.</remarks>
	public async Task<DapResult<TResponse>> SendRequestAsync<TResponse>(IDapRequest<TResponse> request, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var completion = new TaskCompletionSource<DapResult<TResponse>>(TaskCreationOptions.RunContinuationsAsynchronously);
		var pending = new PendingRequest(
			response => completion.TrySetResult(ReadResponse<TResponse>(response)),
			error => completion.TrySetResult(error));

		if (Volatile.Read(ref _stopped) is not 0) return new DapError("The connection was closed.");

		var argumentsTypeInfo = _json.GetTypeInfo(request.GetType());
		var seq = WriteMessage(writer =>
		{
			writer.WriteString("type"u8, "request"u8);
			writer.WriteString("command"u8, request.Command);
			writer.WritePropertyName("arguments"u8);
			JsonSerializer.Serialize(writer, request, argumentsTypeInfo);
		}, pending);
		if (seq is null) return new DapError("The connection was closed.");

		await using var registration = cancellationToken.Register(() =>
		{
			if (!_pendingRequests.TryRemove(seq.Value, out _)) return;
			completion.TrySetCanceled(cancellationToken);
			TrySendCancel(seq.Value);
		}).ConfigureAwait(false);
		return await completion.Task.ConfigureAwait(false);
	}

	/// <summary>Sends a request to the peer and blocks until its response is received.</summary>
	/// <remarks>Must not be called from an event handler, as events are dispatched on the same loop as incoming requests.</remarks>
	public DapResult<TResponse> SendRequest<TResponse>(IDapRequest<TResponse> request, CancellationToken cancellationToken = default)
		=> SendRequestAsync(request, cancellationToken).GetAwaiter().GetResult();

	private void TrySendCancel(int requestSeq)
	{
		try
		{
			_ = SendRequestAsync(new CancelArguments { RequestId = requestSeq });
		}
		catch (Exception ex)
		{
			_log?.Invoke($"Failed to send cancel request for request {requestSeq}: {ex.Message}");
		}
	}

	private DapResult<TResponse> ReadResponse<TResponse>(JsonElement response)
	{
		var success = response.TryGetProperty("success"u8, out var successElement) && successElement.ValueKind is JsonValueKind.True;
		response.TryGetProperty("body"u8, out var body);
		if (success is false) return ReadError(response, body);

		try
		{
			var typeInfo = (JsonTypeInfo<TResponse>)_json.GetTypeInfo(typeof(TResponse));
			var value = body.ValueKind is JsonValueKind.Object
				? body.Deserialize(typeInfo)
				: JsonSerializer.Deserialize(EmptyObject, typeInfo);
			return value is null ? new DapError("The response body was null.") : value;
		}
		catch (JsonException ex)
		{
			return new DapError($"Invalid response body: {ex.Message}");
		}
	}

	private static DapError ReadError(JsonElement response, JsonElement body)
	{
		var message = response.TryGetProperty("message"u8, out var messageElement) ? messageElement.GetString() : null;
		int? id = null;
		var showUser = false;
		if (body.ValueKind is JsonValueKind.Object && body.TryGetProperty("error"u8, out var error) && error.ValueKind is JsonValueKind.Object)
		{
			if (error.TryGetProperty("format"u8, out var format) && format.GetString() is { } formatText)
			{
				message = error.TryGetProperty("variables"u8, out var variables) && variables.ValueKind is JsonValueKind.Object
					? FormatMessage(formatText, variables)
					: formatText;
			}
			if (error.TryGetProperty("id"u8, out var idElement) && idElement.TryGetInt32(out var idValue)) id = idValue;
			showUser = error.TryGetProperty("showUser"u8, out var showUserElement) && showUserElement.ValueKind is JsonValueKind.True;
		}
		return new DapError(message ?? "The request failed.") { Id = id, ShowUser = showUser };

		static string FormatMessage(string format, JsonElement variables)
		{
			foreach (var variable in variables.EnumerateObject())
			{
				format = format.Replace("{" + variable.Name + "}", variable.Value.GetString());
			}
			return format;
		}
	}

	private int? WriteMessage(Action<Utf8JsonWriter> writeFields, PendingRequest? pending = null)
	{
		var buffer = new ArrayBufferWriter<byte>();
		lock (_writeLock)
		{
			if (Volatile.Read(ref _stopped) is not 0) return null;
			var seq = ++_seq;
			using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = _json.Encoder }))
			{
				writer.WriteStartObject();
				writer.WriteNumber("seq"u8, seq);
				writeFields(writer);
				writer.WriteEndObject();
			}
			// Register atomically with respect to Stop, without holding the state lock during serialization or I/O.
			lock (_stateLock)
			{
				if (_stopped is not 0) return null;
				if (pending is not null) _pendingRequests[seq] = pending;
			}

			try
			{
				_output.Write(Encoding.ASCII.GetBytes($"Content-Length: {buffer.WrittenCount}\r\n\r\n"));
				_output.Write(buffer.WrittenSpan);
				_output.Flush();
			}
			catch (Exception ex) when (ex is IOException or ObjectDisposedException)
			{
				_log?.Invoke($"Failed to write message: {ex.Message}");
				Stop();
				return null;
			}
			return seq;
		}
	}

	private bool WriteResponse(int requestSeq, string command, DapResponse response)
	{
		return WriteMessage(writer =>
		{
			writer.WriteString("type"u8, "response"u8);
			writer.WriteNumber("request_seq"u8, requestSeq);
			writer.WriteBoolean("success"u8, response.Error is null);
			writer.WriteString("command"u8, command);
			if (response.Error is { } error)
			{
				writer.WriteString("message"u8, error.Message);
				writer.WriteStartObject("body"u8);
				writer.WriteStartObject("error"u8);
				writer.WriteNumber("id"u8, error.Id ?? 0);
				writer.WriteString("format"u8, error.Message);
				writer.WriteBoolean("showUser"u8, error.ShowUser);
				writer.WriteEndObject();
				writer.WriteEndObject();
			}
			else if (response.Body is not null)
			{
				writer.WritePropertyName("body"u8);
				JsonSerializer.Serialize(writer, response.Body, _json.GetTypeInfo(response.BodyType!));
			}
		}) is not null;
	}

	private void ReadLoop()
	{
		var reader = new MessageReader(_input);
		try
		{
			while (Volatile.Read(ref _stopped) is 0 && reader.TryReadMessage(out var message))
			{
				try
				{
					using var document = JsonDocument.Parse(message);
					OnMessage(document.RootElement);
				}
				catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
				{
					ReportProtocolError(new InvalidDataException($"Received a malformed message: {ex.Message}", ex));
				}
			}
		}
		catch (Exception ex) when (Volatile.Read(ref _stopped) is 0)
		{
			ReportProtocolError(ex);
		}
		catch
		{
			// Reading failed because the connection was stopped
		}
		finally
		{
			Stop();
		}
	}

	private void OnMessage(JsonElement message)
	{
		var type = message.GetProperty("type"u8);
		switch (type)
		{
			case var _ when type.ValueEquals("response"u8):
				var requestSeq = message.GetProperty("request_seq"u8).GetInt32();
				if (_pendingRequests.TryRemove(requestSeq, out var pending)) pending.Complete(message);
				else _log?.Invoke($"Received a response to unknown request {requestSeq}");
				break;
			case var _ when type.ValueEquals("request"u8):
				OnRequest(message);
				break;
			case var _ when type.ValueEquals("event"u8):
				OnEvent(message);
				break;
			default:
				_log?.Invoke($"Received a message with unknown type '{type}'");
				break;
		}
	}

	private void OnRequest(JsonElement message)
	{
		var seq = message.GetProperty("seq"u8).GetInt32();
		var command = message.GetProperty("command"u8).GetString() ?? "";
		message.TryGetProperty("arguments"u8, out var arguments);

		if (command is "cancel")
		{
			// Handled on the reader so it is not queued behind the request it cancels
			if (arguments.ValueKind is JsonValueKind.Object && arguments.TryGetProperty("requestId"u8, out var requestId)
				&& requestId.TryGetInt32(out var cancelledSeq) && _inflightRequests.TryGetValue(cancelledSeq, out var cts))
			{
				try { cts.Cancel(); } catch (ObjectDisposedException) { /* the request completed concurrently */ }
			}
			WriteResponse(seq, command, DapResponse.Success(new CancelResponse()));
			return;
		}

		IDapRequest request;
		if (_requestTypes.TryGetValue(command, out var requestType))
		{
			try
			{
				var typeInfo = _json.GetTypeInfo(requestType);
				request = (IDapRequest)(arguments.ValueKind is JsonValueKind.Object
					? arguments.Deserialize(typeInfo)
					: JsonSerializer.Deserialize(EmptyObject, typeInfo))!;
			}
			catch (JsonException ex)
			{
				WriteResponse(seq, command, DapResponse.Failure(new DapError($"Invalid arguments for request '{command}': {ex.Message}")));
				return;
			}
		}
		else
		{
			request = new UnknownRequest(command, arguments.ValueKind is JsonValueKind.Undefined ? null : arguments.Clone());
		}
		var incoming = new IncomingRequest(seq, command, request, CancellationTokenSource.CreateLinkedTokenSource(_stopCts.Token));
		_inflightRequests[seq] = incoming.CancellationSource;
		if (!_incoming.Writer.TryWrite(incoming)) ReleaseRequest(seq, incoming.CancellationSource);
	}

	private void OnEvent(JsonElement message)
	{
		var name = message.GetProperty("event"u8).GetString() ?? "";
		if (!_eventSubscriptions.TryGetValue(name, out var subscriptions) || subscriptions.Length is 0) return;
		if (!_eventTypes.TryGetValue(name, out var eventType)) return;

		try
		{
			var typeInfo = _json.GetTypeInfo(eventType);
			var body = message.TryGetProperty("body"u8, out var bodyElement) && bodyElement.ValueKind is JsonValueKind.Object
				? bodyElement.Deserialize(typeInfo)
				: JsonSerializer.Deserialize(EmptyObject, typeInfo);
			_incoming.Writer.TryWrite(new IncomingEvent((IDapEvent)body!, subscriptions));
		}
		catch (JsonException ex)
		{
			_log?.Invoke($"Failed to deserialize event '{name}': {ex.Message}");
		}
	}

	private async Task DispatchLoopAsync()
	{
		await foreach (var message in _incoming.Reader.ReadAllAsync().ConfigureAwait(false))
		{
			// Passing this check admits the message to dispatch. Stop cancels admitted requests and discards queued ones.
			if (Volatile.Read(ref _stopped) is not 0)
			{
				if (message is IncomingRequest discarded) ReleaseRequest(discarded.Seq, discarded.CancellationSource);
				continue;
			}
			switch (message)
			{
				case IncomingRequest request:
					DispatchRequest(request);
					break;
				case IncomingEvent incomingEvent:
					foreach (var subscription in incomingEvent.Subscriptions)
					{
						if (Volatile.Read(ref _stopped) is not 0) break;
						try
						{
							subscription.Invoke(incomingEvent.Event);
						}
						catch (Exception ex)
						{
							_log?.Invoke($"Event handler for '{incomingEvent.Event.Event}' failed: {ex}");
						}
					}
					break;
			}
		}
	}

	private void DispatchRequest(IncomingRequest incoming)
	{
		var cts = incoming.CancellationSource;
		var context = new DapRequestContext(this, incoming.Seq, incoming.Command, cts.Token);

		ValueTask<DapResponse> pending;
		try
		{
			context.CancellationToken.ThrowIfCancellationRequested();
			pending = _handler is null ? DapResponse.NotSupported(incoming.Request) : _handler.HandleRequestAsync(incoming.Request, context);
		}
		catch (Exception ex)
		{
			pending = ValueTask.FromResult(ResponseFromException(ex, context));
		}

		if (pending.IsCompletedSuccessfully) CompleteRequest(context, cts, pending.Result);
		else _ = CompleteRequestAsync(context, cts, pending);
	}

	private async Task CompleteRequestAsync(DapRequestContext context, CancellationTokenSource cts, ValueTask<DapResponse> pending)
	{
		DapResponse response;
		try
		{
			response = await pending.ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			response = ResponseFromException(ex, context);
		}
		CompleteRequest(context, cts, response);
	}

	private void CompleteRequest(DapRequestContext context, CancellationTokenSource cts, DapResponse response)
	{
		ReleaseRequest(context.Seq, cts);
		if (Volatile.Read(ref _stopped) is not 0) return;

		try
		{
			if (!WriteResponse(context.Seq, context.Command, response)) return;
		}
		catch (Exception ex)
		{
			// e.g. the response body could not be serialized
			_log?.Invoke($"Failed to send the response to '{context.Command}': {ex}");
			if (!WriteResponse(context.Seq, context.Command, DapResponse.Failure(new DapError(ex.Message)))) return;
		}

		try
		{
			context.ResponseSent();
		}
		catch (Exception ex)
		{
			_log?.Invoke($"Response callback for '{context.Command}' failed: {ex}");
		}
	}

	private void ReleaseRequest(int seq, CancellationTokenSource cts)
	{
		_inflightRequests.TryRemove(seq, out _);
		cts.Dispose();
	}

	private DapResponse ResponseFromException(Exception ex, DapRequestContext context)
	{
		if (ex is OperationCanceledException && context.CancellationToken.IsCancellationRequested) return DapResponse.Failure(DapError.Cancelled);
		_log?.Invoke($"Unhandled exception in handler for '{context.Command}': {ex}");
		return DapResponse.Failure(new DapError(ex.Message));
	}

	private void ReportProtocolError(Exception ex)
	{
		_log?.Invoke($"DAP protocol error: {ex}");
		ProtocolError?.Invoke(ex);
	}

	private void Unsubscribe(string name, EventSubscription subscription)
	{
		while (_eventSubscriptions.TryGetValue(name, out var existing))
		{
			var updated = existing.Where(s => s != subscription).ToArray();
			if (_eventSubscriptions.TryUpdate(name, updated, existing)) return;
		}
	}

	private abstract record IncomingMessage;
	private sealed record IncomingRequest(int Seq, string Command, IDapRequest Request, CancellationTokenSource CancellationSource) : IncomingMessage;
	private sealed record IncomingEvent(IDapEvent Event, EventSubscription[] Subscriptions) : IncomingMessage;

	private sealed class PendingRequest(Action<JsonElement> complete, Action<DapError> fail)
	{
		public void Complete(JsonElement response)
		{
			try
			{
				complete(response);
			}
			catch (Exception ex)
			{
				// The request has already been removed from the pending dictionary, so Stop cannot complete it.
				Fail(new DapError($"Invalid response: {ex.Message}"));
				throw;
			}
		}
		public void Fail(DapError error) => fail(error);
	}

	private sealed class EventSubscription(DapConnection connection, string name, Action<IDapEvent> handler) : IDisposable
	{
		public void Invoke(IDapEvent @event) => handler(@event);
		public void Dispose() => connection.Unsubscribe(name, this);
	}

	/// <summary>Reads <c>Content-Length</c> framed messages from a stream.</summary>
	private sealed class MessageReader(Stream stream)
	{
		private byte[] _buffer = new byte[64 * 1024];
		private int _start;
		private int _end;

		public bool TryReadMessage(out ReadOnlyMemory<byte> message)
		{
			message = default;
			int headerLength;
			while ((headerLength = _buffer.AsSpan(_start, _end - _start).IndexOf(HeaderTerminator)) < 0)
			{
				if (!Fill()) return false;
			}

			var contentLength = ParseContentLength(Encoding.ASCII.GetString(_buffer, _start, headerLength));
			_start += headerLength + HeaderTerminator.Length;
			while (_end - _start < contentLength)
			{
				if (!Fill(contentLength)) return false;
			}

			// The returned memory remains valid until the next call
			message = _buffer.AsMemory(_start, contentLength);
			_start += contentLength;
			return true;
		}

		private bool Fill(int required = 0)
		{
			if (_start > 0)
			{
				Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
				_end -= _start;
				_start = 0;
			}
			if (_end == _buffer.Length || required > _buffer.Length)
			{
				Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, required));
			}
			var read = stream.Read(_buffer, _end, _buffer.Length - _end);
			if (read is 0) return false;
			_end += read;
			return true;
		}

		private static int ParseContentLength(string header)
		{
			foreach (var line in header.Split("\r\n"))
			{
				var separator = line.IndexOf(':');
				if (separator < 0) continue;
				if (line.AsSpan(0, separator).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
					&& int.TryParse(line.AsSpan(separator + 1).Trim(), out var length))
				{
					return length;
				}
			}
			throw new InvalidDataException($"Message header did not contain a valid Content-Length: '{header}'");
		}
	}
}
