using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SharpDbg.Infrastructure.Debugger;
using SharpDbg.DapProtocol;

namespace SharpDbg.Application.Protocol;

/// <summary>
/// SharpDbg-specific request that resolves (decompiling if necessary) the source location of a stack frame
/// that was returned unresolved from a <c>stackTrace</c> request.
/// </summary>
public sealed record ResolveStackFrameArguments : IDapRequest<ResolveStackFrameResponse>, IDapNamedMessage
{
	public const string CommandName = "resolveStackFrame";
	static string IDapNamedMessage.MessageName => CommandName;
	string IDapRequest.Command => CommandName;

	[JsonPropertyName("stackFrameId")]
	public int StackFrameId { get; init; }
}

public sealed record ResolveStackFrameResponse
{
	[JsonPropertyName("stackFrame")]
	public required StackFrame StackFrame { get; init; }
}

/// <summary>
/// Visual Studio sends 'vsCustomMessage' requests, which SharpDbg acknowledges without acting on.
/// </summary>
public sealed record VsCustomMessageArguments : IDapRequest<VsCustomMessageResponse>, IDapNamedMessage
{
	static string IDapNamedMessage.MessageName => "vsCustomMessage";
	string IDapRequest.Command => "vsCustomMessage";

	[JsonExtensionData]
	public JsonObject? AdditionalProperties { get; set; }
}

public sealed record VsCustomMessageResponse;

/// <summary>
/// SharpDbg-specific stack frame properties, sent in addition to the protocol-defined ones.
/// </summary>
public static class StackFrameExtensions
{
	extension(StackFrame stackFrame)
	{
		public bool? IsResolved
		{
			get => stackFrame.AdditionalProperties?["isResolved"]?.GetValue<bool>();
			set => SetAdditionalProperty(stackFrame, "isResolved", value);
		}

		/// <remarks>Serialized with default (PascalCase) property names, for compatibility with existing clients.</remarks>
		public DecompiledSourceInfo? DecompiledSourceInfo
		{
			get => stackFrame.AdditionalProperties?["decompiledSourceInfo"]?.Deserialize<DecompiledSourceInfo>();
			set => SetAdditionalProperty(stackFrame, "decompiledSourceInfo", value is null ? null : JsonSerializer.SerializeToNode(value));
		}
	}

	private static void SetAdditionalProperty(StackFrame stackFrame, string name, JsonNode? value)
	{
		if (value is null)
		{
			stackFrame.AdditionalProperties?.Remove(name);
			return;
		}
		(stackFrame.AdditionalProperties ??= [])[name] = value;
	}
}
