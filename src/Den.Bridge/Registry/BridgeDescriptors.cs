using System.Text.Json;
using Den.Bridge.Abstractions;
using Den.Bridge.Protocol;

namespace Den.Bridge.Registry;

public delegate ValueTask<JsonElement?> BridgeCommandInvocationDelegate(
    IServiceProvider serviceProvider,
    JsonElement payload,
    BridgeRequestContext context,
    CancellationToken cancellationToken);

public sealed record BridgeCommandDescriptor
{
    public BridgeCommandDescriptor(
        string command,
        Type requestType,
        Type responseType,
        Type handlerType,
        BridgeCommandInvocationDelegate invokeAsync,
        bool supportsCancellation = false,
        bool supportsProgress = false,
        IReadOnlyList<string>? requiredCapabilities = null,
        string? requestSchema = null,
        string? responseSchema = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(requestType);
        ArgumentNullException.ThrowIfNull(responseType);
        ArgumentNullException.ThrowIfNull(handlerType);
        ArgumentNullException.ThrowIfNull(invokeAsync);

        Command = command;
        RequestType = requestType;
        ResponseType = responseType;
        HandlerType = handlerType;
        InvokeAsync = invokeAsync;
        SupportsCancellation = supportsCancellation;
        SupportsProgress = supportsProgress;
        RequiredCapabilities = NormalizeList(requiredCapabilities);
        RequestSchema = requestSchema;
        ResponseSchema = responseSchema;
    }

    public string Command { get; }

    public Type RequestType { get; }

    public Type ResponseType { get; }

    public Type HandlerType { get; }

    public BridgeCommandInvocationDelegate InvokeAsync { get; }

    public bool SupportsCancellation { get; }

    public bool SupportsProgress { get; }

    public IReadOnlyList<string> RequiredCapabilities { get; }

    public string? RequestSchema { get; }

    public string? ResponseSchema { get; }

    public BridgeCommandCapability ToCapability()
    {
        return new BridgeCommandCapability
        {
            Command = Command,
            RequestSchema = RequestSchema,
            ResponseSchema = ResponseSchema,
            SupportsCancellation = SupportsCancellation,
            SupportsProgress = SupportsProgress,
            RequiredCapabilities = RequiredCapabilities,
        };
    }

    private static IReadOnlyList<string> NormalizeList(IReadOnlyList<string>? values)
    {
        return values is { Count: > 0 }
            ? values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();
    }
}

public sealed record BridgeEventDescriptor
{
    public BridgeEventDescriptor(
        string @event,
        Type payloadType,
        IReadOnlyList<string>? requiredCapabilities = null,
        string? payloadSchema = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(@event);
        ArgumentNullException.ThrowIfNull(payloadType);

        Event = @event;
        PayloadType = payloadType;
        RequiredCapabilities = NormalizeList(requiredCapabilities);
        PayloadSchema = payloadSchema;
    }

    public string Event { get; }

    public Type PayloadType { get; }

    public IReadOnlyList<string> RequiredCapabilities { get; }

    public string? PayloadSchema { get; }

    public BridgeEventCapability ToCapability()
    {
        return new BridgeEventCapability
        {
            Event = Event,
            PayloadSchema = PayloadSchema,
            RequiredCapabilities = RequiredCapabilities.Count == 0 ? null : RequiredCapabilities,
        };
    }

    private static IReadOnlyList<string> NormalizeList(IReadOnlyList<string>? values)
    {
        return values is { Count: > 0 }
            ? values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();
    }
}
