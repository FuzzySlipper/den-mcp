using System.Text.Json;
using System.Text.Json.Serialization;

namespace Den.Bridge.Protocol;

public static class BridgeJson
{
    public static JsonSerializerOptions SerializerOptions { get; } = CreateSerializerOptions();

    public static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value, SerializerOptions);
    }

    public static T? Deserialize<T>(string json)
    {
        return JsonSerializer.Deserialize<T>(json, SerializerOptions);
    }

    public static JsonElement ToElement<T>(T value)
    {
        return JsonSerializer.SerializeToElement(value, SerializerOptions);
    }

    public static JsonElement EmptyObject()
    {
        return ToElement(new Dictionary<string, object?>());
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        return new JsonSerializerOptions(JsonSerializerDefaults.General)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = false,
            WriteIndented = false,
        };
    }
}
