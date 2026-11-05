namespace SharpDbg.DapProtocol;

/// <summary>
/// An error returned from a request handler, or received in response to a request.
/// Sent to the peer as an <c>ErrorResponse</c>.
/// </summary>
/// <param name="Message">The error message. Sent as both the response's short <c>message</c> and the <c>error.format</c> shown to users.</param>
public sealed record DapError(string Message)
{
	/// <summary>Optional unique identifier for this kind of error.</summary>
	public int? Id { get; init; }

	/// <summary>If true, the client should show the error to the user.</summary>
	public bool ShowUser { get; init; }

	/// <summary>The error returned when a request is cancelled.</summary>
	public static DapError Cancelled { get; } = new("cancelled");
}

/// <summary>
/// The outcome of a request: either the response body <typeparamref name="T"/>, or a <see cref="DapError"/>.
/// </summary>
public union DapResult<T>(T, DapError);
