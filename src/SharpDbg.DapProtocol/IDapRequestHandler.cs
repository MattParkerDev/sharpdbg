namespace SharpDbg.DapProtocol;

/// <summary>
/// Handles requests received over a <see cref="DapConnection"/>.
/// </summary>
/// <example>
/// <code>
/// public ValueTask&lt;DapResponse&gt; HandleRequestAsync(IDapRequest request, DapRequestContext context) => request switch
/// {
///     InitializeArguments args => DapResponse.Respond(args, Initialize),
///     StackTraceArguments args => DapResponse.Respond(args, StackTraceAsync, context.CancellationToken),
///     _ => DapResponse.NotSupported(request),
/// };
/// </code>
/// </example>
public interface IDapRequestHandler
{
	/// <summary>
	/// Handles a request. Requests are dispatched in the order they are received: this method is invoked on the
	/// connection's dispatch loop, so the next request is not dispatched until this method returns (or yields at its first await).
	/// </summary>
	ValueTask<DapResponse> HandleRequestAsync(IDapRequest request, DapRequestContext context);
}

/// <summary>
/// Information about the request being handled.
/// </summary>
public sealed class DapRequestContext
{
	private List<Action>? _onResponseSent;

	internal DapRequestContext(DapConnection connection, int seq, string command, CancellationToken cancellationToken)
	{
		Connection = connection;
		Seq = seq;
		Command = command;
		CancellationToken = cancellationToken;
	}

	/// <summary>The connection the request was received on.</summary>
	public DapConnection Connection { get; }

	/// <summary>The sequence number of the request.</summary>
	public int Seq { get; }

	/// <summary>The request command.</summary>
	public string Command { get; }

	/// <summary>Cancelled when the peer sends a <c>cancel</c> request for this request, or when the connection stops.</summary>
	public CancellationToken CancellationToken { get; }

	/// <summary>
	/// Runs <paramref name="action"/> once the response to this request has been written,
	/// e.g. to send the <c>initialized</c> event after the <c>initialize</c> response.
	/// </summary>
	public void OnResponseSent(Action action) => (_onResponseSent ??= []).Add(action);

	internal void ResponseSent()
	{
		if (_onResponseSent is null) return;
		foreach (var action in _onResponseSent) action();
	}
}

/// <summary>
/// A response to send to the peer, created from a handler's <see cref="DapResult{T}"/>.
/// </summary>
public sealed class DapResponse
{
	private DapResponse(object? body, Type? bodyType, DapError? error)
	{
		Body = body;
		BodyType = bodyType;
		Error = error;
	}

	/// <summary>The response body, if the request succeeded.</summary>
	public object? Body { get; }

	/// <summary>The type used to serialize <see cref="Body"/>.</summary>
	public Type? BodyType { get; }

	/// <summary>The error, if the request failed.</summary>
	public DapError? Error { get; }

	/// <summary>Creates a successful response.</summary>
	public static DapResponse Success<T>(T body) where T : notnull => new(body, typeof(T), null);

	/// <summary>Creates an error response.</summary>
	public static DapResponse Failure(DapError error) => new(null, null, error);

	/// <summary>Creates a response from a handler result.</summary>
	public static DapResponse From<T>(DapResult<T> dapResult) where T : notnull => dapResult switch
	{
		DapError error => Failure(error),
		T body => Success(body),
		null => throw new InvalidOperationException("Result was not initialized"),
	};

	/// <summary>The error response for a request this adapter does not support.</summary>
	public static ValueTask<DapResponse> NotSupported(IDapRequest request) =>
		ValueTask.FromResult(Failure(new DapError($"Request '{request.Command}' is not supported.")));

	/// <summary>Handles <paramref name="arguments"/> with a synchronous handler.</summary>
	public static ValueTask<DapResponse> Respond<TArguments, TResponse>(TArguments arguments, Func<TArguments, DapResult<TResponse>> handler)
		where TArguments : IDapRequest<TResponse> where TResponse : notnull
		=> ValueTask.FromResult(From(handler(arguments)));

	/// <summary>Handles <paramref name="arguments"/> with an asynchronous handler.</summary>
	public static async ValueTask<DapResponse> Respond<TArguments, TResponse>(TArguments arguments, Func<TArguments, ValueTask<DapResult<TResponse>>> handler)
		where TArguments : IDapRequest<TResponse> where TResponse : notnull
		=> From(await handler(arguments).ConfigureAwait(false));

	/// <summary>Handles <paramref name="arguments"/> with an asynchronous, cancellable handler.</summary>
	public static async ValueTask<DapResponse> Respond<TArguments, TResponse>(TArguments arguments, Func<TArguments, CancellationToken, ValueTask<DapResult<TResponse>>> handler, CancellationToken cancellationToken)
		where TArguments : IDapRequest<TResponse> where TResponse : notnull
		=> From(await handler(arguments, cancellationToken).ConfigureAwait(false));
}
