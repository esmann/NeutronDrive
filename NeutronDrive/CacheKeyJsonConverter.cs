using System.Text.Json;
using System.Text.Json.Serialization;
using Proton.Sdk;

namespace NeutronDrive; // Using your namespace from the stack trace

public class CacheKeyJsonConverter : JsonConverter<CacheKey>
{
    // --- 1. Standard Value Serialization (e.g., serializing a single CacheKey) ---
    public override CacheKey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Expected string value for CacheKey.");

        return ParseCacheKey(reader.GetString());
    }

    public override void Write(Utf8JsonWriter writer, CacheKey value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(FormatCacheKey(value));
    }

    // --- 2. Dictionary Key Serialization (e.g., Dictionary<CacheKey, string>) ---
    public override CacheKey ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // reader.GetString() here gets the JSON property name
        return ParseCacheKey(reader.GetString());
    }

    public override void WriteAsPropertyName(Utf8JsonWriter writer, CacheKey value, JsonSerializerOptions options)
    {
        // Writes the key on the left side of the JSON object, like: "App:123:User:456:Prefs": "my value"
        writer.WritePropertyName(FormatCacheKey(value));
    }

    // --- Helper Methods to keep logic in one place ---
    private CacheKey ParseCacheKey(string? stringValue)
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

    private string FormatCacheKey(CacheKey value)
    {
        // Uses the logic you provided earlier (assuming no ToString() method is available)
        return value.Context is not { } context
            ? $"{value.ValueHolderName}:{value.ValueHolderId}:{value.ValueName}"
            : $"{context.Name}:{context.Id}:{value.ValueHolderName}:{value.ValueHolderId}:{value.ValueName}";
    }
}