using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpDbg.DapProtocol;

/// <summary>
/// A string-valued protocol enumeration. Unknown values are preserved rather than rejected.
/// </summary>
public interface IDapStringEnum<TSelf> where TSelf : struct, IDapStringEnum<TSelf>
{
	/// <summary>The value as it appears on the wire.</summary>
	string Name { get; }

	/// <summary>Creates a value from its wire representation.</summary>
	static abstract TSelf FromName(string name);
}

/// <summary>Serializes <see cref="IDapStringEnum{TSelf}"/> values as JSON strings.</summary>
public sealed class DapStringEnumConverter<T> : JsonConverter<T> where T : struct, IDapStringEnum<T>
{
	/// <inheritdoc/>
	public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		var name = reader.GetString() ?? throw new JsonException($"Expected a string value for {typeof(T).Name}");
		return T.FromName(name);
	}

	/// <inheritdoc/>
	public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
	{
		if (value.Name is null) writer.WriteNullValue();
		else writer.WriteStringValue(value.Name);
	}
}

/// <summary>
/// A protocol value that may be either an integer or a string, e.g. <see cref="Module.Id"/>.
/// </summary>
[JsonConverter(typeof(IntegerOrStringConverter))]
public readonly record struct IntegerOrString
{
	private IntegerOrString(int? integer, string? text)
	{
		Integer = integer;
		String = text;
	}

	/// <summary>The integer value, if this value is an integer.</summary>
	public int? Integer { get; }

	/// <summary>The string value, if this value is a string.</summary>
	public string? String { get; }

	/// <summary>Creates an integer value.</summary>
	public static implicit operator IntegerOrString(int value) => new(value, null);
	/// <summary>Creates a string value.</summary>
	public static implicit operator IntegerOrString(string value) => new(null, value);

	/// <inheritdoc/>
	public override string ToString() => String ?? Integer?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
}

internal sealed class IntegerOrStringConverter : JsonConverter<IntegerOrString>
{
	public override IntegerOrString Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
	{
		JsonTokenType.Number => reader.GetInt32(),
		JsonTokenType.String => reader.GetString()!,
		_ => throw new JsonException($"Expected an integer or string, got {reader.TokenType}"),
	};

	public override void Write(Utf8JsonWriter writer, IntegerOrString value, JsonSerializerOptions options)
	{
		if (value.String is not null) writer.WriteStringValue(value.String);
		else if (value.Integer is not null) writer.WriteNumberValue(value.Integer.Value);
		else writer.WriteNullValue();
	}
}
