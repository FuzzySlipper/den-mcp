using System.Text.Json;
using Den.Bridge.Abstractions;
using Den.Bridge.Protocol;
using Microsoft.Extensions.DependencyInjection;

namespace Den.Bridge.Registry;

public sealed class BridgeRegistryBuilder
{
    private readonly List<BridgeCommandDescriptor> _commands = [];
    private readonly List<BridgeEventDescriptor> _events = [];

    public IReadOnlyList<BridgeCommandDescriptor> Commands => _commands;

    public IReadOnlyList<BridgeEventDescriptor> Events => _events;

    public BridgeRegistryBuilder RegisterCommand<TRequest, TResponse, THandler>(
        string command,
        Action<BridgeCommandRegistration>? configure = null)
        where THandler : class, IBridgeCommandHandler<TRequest, TResponse>
    {
        var registration = new BridgeCommandRegistration(command)
        {
            RequestSchema = command + ".request",
            ResponseSchema = command + ".response",
        };
        configure?.Invoke(registration);

        _commands.Add(new BridgeCommandDescriptor(
            command,
            typeof(TRequest),
            typeof(TResponse),
            typeof(THandler),
            InvokeTypedAsync<TRequest, TResponse, THandler>,
            registration.SupportsCancellation,
            registration.SupportsProgress,
            registration.RequiredCapabilities,
            registration.RequestSchema,
            registration.ResponseSchema));

        return this;
    }

    public BridgeRegistryBuilder RegisterEvent<TPayload>(
        string @event,
        Action<BridgeEventRegistration>? configure = null)
    {
        var registration = new BridgeEventRegistration(@event)
        {
            PayloadSchema = @event + ".payload",
        };
        configure?.Invoke(registration);

        _events.Add(new BridgeEventDescriptor(
            @event,
            typeof(TPayload),
            registration.RequiredCapabilities,
            registration.PayloadSchema));

        return this;
    }

    public BridgeCommandRegistry BuildCommandRegistry()
    {
        return new BridgeCommandRegistry(_commands.ToArray());
    }

    public BridgeEventRegistry BuildEventRegistry()
    {
        return new BridgeEventRegistry(_events.ToArray());
    }

    private static async ValueTask<JsonElement?> InvokeTypedAsync<TRequest, TResponse, THandler>(
        IServiceProvider serviceProvider,
        JsonElement payload,
        BridgeRequestContext context,
        CancellationToken cancellationToken)
        where THandler : class, IBridgeCommandHandler<TRequest, TResponse>
    {
        var handler = serviceProvider.GetRequiredService<THandler>();
        var request = payload.Deserialize<TRequest>(BridgeJson.SerializerOptions);
        if (request is null)
        {
            throw new JsonException($"Bridge command '{context.RequestId}' payload deserialized to null.");
        }

        var response = await handler.HandleAsync(request, context, cancellationToken).ConfigureAwait(false);
        return response is null ? null : BridgeJson.ToElement(response);
    }
}

public sealed class BridgeCommandRegistration
{
    internal BridgeCommandRegistration(string command)
    {
        Command = command;
    }

    public string Command { get; }

    public bool SupportsCancellation { get; set; }

    public bool SupportsProgress { get; set; }

    public IReadOnlyList<string> RequiredCapabilities { get; set; } = Array.Empty<string>();

    public string? RequestSchema { get; set; }

    public string? ResponseSchema { get; set; }
}

public sealed class BridgeEventRegistration
{
    internal BridgeEventRegistration(string @event)
    {
        Event = @event;
    }

    public string Event { get; }

    public IReadOnlyList<string> RequiredCapabilities { get; set; } = Array.Empty<string>();

    public string? PayloadSchema { get; set; }
}
