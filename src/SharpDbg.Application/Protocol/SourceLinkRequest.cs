using System.Text.Json.Serialization;
using SharpDbg.DapProtocol;

namespace SharpDbg.Application.Protocol;

/// <summary>
/// SharpDbg-specific request that returns the SourceLink URL for a source file, for clients that want to open
/// the original source from the repository.
/// </summary>
public sealed record SourceLinkArguments : IDapRequest<SourceLinkResponse>, IDapNamedMessage
{
	public const string CommandName = "sourceLink";
	static string IDapNamedMessage.MessageName => CommandName;
	string IDapRequest.Command => CommandName;

	[JsonPropertyName("path")]
	public required string Path { get; init; }
}

public sealed record SourceLinkResponse
{
	[JsonPropertyName("url")]
	public string? Url { get; init; }
}
