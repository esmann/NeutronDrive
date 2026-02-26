using System.Text.Json;
using System.Text.Json.Serialization;
using Proton.Sdk;

namespace NeutronDrive; // Using your namespace from the stack trace

public class CacheKeyJsonConverter : JsonConverter<CacheKey>
{
    public override CacheKey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType != JsonTokenType.String ? throw new JsonException("Expected string value for CacheKey.") : ParseCacheKey(reader.GetString());
    }

    public override void Write(Utf8JsonWriter writer, CacheKey value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }

    public override CacheKey ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return ParseCacheKey(reader.GetString());
    }

    public override void WriteAsPropertyName(Utf8JsonWriter writer, CacheKey value, JsonSerializerOptions options)
    {
        writer.WritePropertyName(value.ToString());
    }

    private static CacheKey ParseCacheKey(string? stringValue)
    {
        if (string.IsNullOrWhiteSpace(stringValue))
        {
            throw new JsonException("CacheKey string cannot be empty.");
        }

        var parts = stringValue.Split(':');

        return parts.Length switch
        {
            3 => new CacheKey(parts[0], parts[1], parts[2]),
            5 => new CacheKey(parts[0], parts[1], parts[2], parts[3], parts[4]),
            _ => throw new JsonException($"Invalid CacheKey format. Expected 3 or 5 parts, but got {parts.Length}.")
        };
    }
}