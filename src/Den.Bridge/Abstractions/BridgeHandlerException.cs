using System.Text.Json;
using Den.Bridge.Protocol;

namespace Den.Bridge.Abstractions;

public sealed class BridgeHandlerException : Exception
{
    public BridgeHandlerException(
        string code,
        string message,
        string category = BridgeErrorCategories.Internal,
        bool retryable = false,
        JsonElement? details = null,
        IReadOnlyList<BridgeError>? causedBy = null)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);

        Code = code;
        Category = category;
        Retryable = retryable;
        Details = details;
        CausedBy = causedBy;
    }

    public string Code { get; }

    public string Category { get; }

    public bool Retryable { get; }

    public JsonElement? Details { get; }

    public IReadOnlyList<BridgeError>? CausedBy { get; }
}
