using System.Collections.ObjectModel;

namespace Den.Bridge.Registry;

public interface IBridgeCommandRegistry
{
    IReadOnlyList<BridgeCommandDescriptor> Commands { get; }

    bool TryGetCommand(string command, out BridgeCommandDescriptor descriptor);
}

public interface IBridgeEventRegistry
{
    IReadOnlyList<BridgeEventDescriptor> Events { get; }

    bool TryGetEvent(string @event, out BridgeEventDescriptor descriptor);
}

public sealed class BridgeCommandRegistry : IBridgeCommandRegistry
{
    private readonly IReadOnlyDictionary<string, BridgeCommandDescriptor> _commandsByName;

    public BridgeCommandRegistry(IEnumerable<BridgeCommandDescriptor> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);

        var commandsByName = new Dictionary<string, BridgeCommandDescriptor>(StringComparer.Ordinal);
        foreach (var descriptor in commands)
        {
            if (!commandsByName.TryAdd(descriptor.Command, descriptor))
            {
                throw new InvalidOperationException($"Bridge command '{descriptor.Command}' is already registered.");
            }
        }

        _commandsByName = new ReadOnlyDictionary<string, BridgeCommandDescriptor>(commandsByName);
        Commands = commandsByName.Values.OrderBy(descriptor => descriptor.Command, StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<BridgeCommandDescriptor> Commands { get; }

    public bool TryGetCommand(string command, out BridgeCommandDescriptor descriptor)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            descriptor = null!;
            return false;
        }

        return _commandsByName.TryGetValue(command, out descriptor!);
    }
}

public sealed class BridgeEventRegistry : IBridgeEventRegistry
{
    private readonly IReadOnlyDictionary<string, BridgeEventDescriptor> _eventsByName;

    public BridgeEventRegistry(IEnumerable<BridgeEventDescriptor> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var eventsByName = new Dictionary<string, BridgeEventDescriptor>(StringComparer.Ordinal);
        foreach (var descriptor in events)
        {
            if (!eventsByName.TryAdd(descriptor.Event, descriptor))
            {
                throw new InvalidOperationException($"Bridge event '{descriptor.Event}' is already registered.");
            }
        }

        _eventsByName = new ReadOnlyDictionary<string, BridgeEventDescriptor>(eventsByName);
        Events = eventsByName.Values.OrderBy(descriptor => descriptor.Event, StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<BridgeEventDescriptor> Events { get; }

    public bool TryGetEvent(string @event, out BridgeEventDescriptor descriptor)
    {
        if (string.IsNullOrWhiteSpace(@event))
        {
            descriptor = null!;
            return false;
        }

        return _eventsByName.TryGetValue(@event, out descriptor!);
    }
}
