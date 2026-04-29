namespace Den.Bridge.Protocol;

public static class BridgeProtocol
{
    public const string ProtocolVersion = "1.0";
    public const string DefaultSchemaVersion = "den-bridge@1";
}

public static class BridgeFrameTypes
{
    public const string Request = "request";
    public const string Response = "response";
    public const string Event = "event";
    public const string Progress = "progress";
    public const string Cancel = "cancel";
    public const string Health = "health";
    public const string Capabilities = "capabilities";
}

public static class BridgeErrorCodes
{
    public const string CommandUnsupported = "bridge.command.unsupported";
    public const string CapabilityUnsupported = "bridge.capability.unsupported";
    public const string RequestDuplicate = "bridge.request.duplicate";
    public const string RequestInvalid = "bridge.request.invalid";
    public const string RequestCancelled = "bridge.request.cancelled";
    public const string HandlerFailed = "bridge.handler.failed";
}

public static class BridgeErrorCategories
{
    public const string Validation = "validation";
    public const string NotFound = "not_found";
    public const string Conflict = "conflict";
    public const string Unauthorized = "unauthorized";
    public const string Transient = "transient";
    public const string Cancelled = "cancelled";
    public const string Internal = "internal";
    public const string Unavailable = "unavailable";
    public const string UnsupportedCapability = "unsupported_capability";
}
