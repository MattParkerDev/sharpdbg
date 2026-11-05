using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace SharpDbg.DapProtocol;

/// <summary>
/// JSON serialization settings for protocol messages.
/// </summary>
public static class DapJson
{
	/// <summary>
	/// Creates serializer options for protocol messages. Custom request, response and event types are resolved
	/// using <paramref name="additionalResolver"/> when provided, otherwise by reflection (when enabled).
	/// </summary>
	public static JsonSerializerOptions CreateOptions(IJsonTypeInfoResolver? additionalResolver = null)
	{
		additionalResolver ??= JsonSerializer.IsReflectionEnabledByDefault ? CreateReflectionResolver() : null;
		var resolver = additionalResolver is null
			? DapJsonContext.Default
			: JsonTypeInfoResolver.Combine(DapJsonContext.Default, additionalResolver);

		var options = new JsonSerializerOptions
		{
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			// Messages are never embedded in HTML, so there is no need to escape characters such as ' and non-ASCII paths
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
			// `required` members model the schema for constructing messages. Peers do not always send every required
			// property, so missing properties are tolerated rather than failing the whole message.
			TypeInfoResolver = resolver.WithAddedModifier(static typeInfo =>
			{
				foreach (var property in typeInfo.Properties) property.IsRequired = false;
			}),
		};
		options.MakeReadOnly();
		return options;
	}

	/// <summary>The default protocol serializer options.</summary>
	public static JsonSerializerOptions DefaultOptions { get; } = CreateOptions();

	[System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Only used when reflection-based serialization is enabled")]
	[System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Only used when reflection-based serialization is enabled")]
	private static IJsonTypeInfoResolver CreateReflectionResolver() => new DefaultJsonTypeInfoResolver();
}
