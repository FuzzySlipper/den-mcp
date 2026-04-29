using System.Text.Json;

namespace Den.Bridge.Protocol;

public static class BridgeFrameJson
{
    public static string Serialize(IBridgeFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        return frame switch
        {
            BridgeRequestFrame request => BridgeJson.Serialize(request),
            BridgeResponseFrame response => BridgeJson.Serialize(response),
            BridgeEventFrame @event => BridgeJson.Serialize(@event),
            BridgeProgressFrame progress => BridgeJson.Serialize(progress),
            BridgeCancelFrame cancel => BridgeJson.Serialize(cancel),
            BridgeHealthFrame health => BridgeJson.Serialize(health),
            BridgeCapabilitiesFrame capabilities => BridgeJson.Serialize(capabilities),
            _ => throw new NotSupportedException($"Bridge frame type '{frame.GetType().FullName}' is not supported."),
        };
    }

    public static IBridgeFrame Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("frame_type", out var frameTypeElement)
            || frameTypeElement.ValueKind != JsonValueKind.String)
        {
            throw new JsonException("Bridge frame JSON must include a string frame_type property.");
        }

        var frameType = frameTypeElement.GetString();
        return frameType switch
        {
            BridgeFrameTypes.Request => DeserializeConcrete<BridgeRequestFrame>(json),
            BridgeFrameTypes.Response => DeserializeConcrete<BridgeResponseFrame>(json),
            BridgeFrameTypes.Event => DeserializeConcrete<BridgeEventFrame>(json),
            BridgeFrameTypes.Progress => DeserializeConcrete<BridgeProgressFrame>(json),
            BridgeFrameTypes.Cancel => DeserializeConcrete<BridgeCancelFrame>(json),
            BridgeFrameTypes.Health => DeserializeConcrete<BridgeHealthFrame>(json),
            BridgeFrameTypes.Capabilities => DeserializeConcrete<BridgeCapabilitiesFrame>(json),
            _ => throw new JsonException($"Bridge frame_type '{frameType}' is not supported."),
        };
    }

    private static T DeserializeConcrete<T>(string json)
        where T : IBridgeFrame
    {
        return BridgeJson.Deserialize<T>(json)
            ?? throw new JsonException($"Bridge frame JSON did not deserialize to {typeof(T).Name}.");
    }
}
