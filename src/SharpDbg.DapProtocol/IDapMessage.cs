namespace SharpDbg.DapProtocol;

/// <summary>
/// The arguments of a DAP request. The request's command name is available via <see cref="Command"/>.
/// </summary>
public interface IDapRequest
{
	/// <summary>The command this request executes, e.g. <c>stackTrace</c>.</summary>
	string Command { get; }
}

/// <summary>
/// The arguments of a DAP request whose successful response body is <typeparamref name="TResponse"/>.
/// </summary>
public interface IDapRequest<TResponse> : IDapRequest;

/// <summary>
/// The body of a DAP event. The event name is available via <see cref="Event"/>.
/// </summary>
public interface IDapEvent
{
	/// <summary>The event name, e.g. <c>stopped</c>.</summary>
	string Event { get; }
}

/// <summary>
/// Provides the wire name (request command or event name) of a message type, so it can be registered with a <see cref="DapConnection"/>.
/// </summary>
public interface IDapNamedMessage
{
	/// <summary>The request command or event name.</summary>
	static abstract string MessageName { get; }
}

/// <summary>
/// A request whose command is not known to the connection. Unknown requests are still dispatched to the
/// <see cref="IDapRequestHandler"/>, which will typically respond with <see cref="DapResponse.NotSupported"/>.
/// </summary>
public sealed record UnknownRequest(string Command, System.Text.Json.JsonElement? Arguments) : IDapRequest;
