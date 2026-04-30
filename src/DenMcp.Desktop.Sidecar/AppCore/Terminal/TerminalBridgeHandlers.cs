using Den.Bridge.Abstractions;
using Den.Bridge.Protocol;

namespace DenMcp.Desktop.Sidecar;

/// <summary>
/// Bridge command handler for creating backend-neutral terminal OperatorSessions.
/// </summary>
public sealed class TerminalCreateSessionHandler
    : IBridgeCommandHandler<TerminalCreateSessionRequest, TerminalCreateSessionResponse>
{
    private readonly TerminalOperatorSessionService _terminals;

    public TerminalCreateSessionHandler(TerminalOperatorSessionService terminals)
    {
        _terminals = terminals;
    }

    public async ValueTask<TerminalCreateSessionResponse?> HandleAsync(
        TerminalCreateSessionRequest request,
        BridgeRequestContext context,
        CancellationToken cancellationToken)
    {
        var session = await _terminals.CreateAsync(request, cancellationToken).ConfigureAwait(false);
        return new TerminalCreateSessionResponse
        {
            Session = TerminalSessionSummaryProjection.FromSession(session),
        };
    }
}

/// <summary>
/// Bridge command handler for listing OperatorSessions.
/// Returns typed summaries from the local registry.
/// </summary>
public sealed class TerminalListSessionsHandler
    : IBridgeCommandHandler<TerminalListSessionsRequest, TerminalListSessionsResponse>
{
    private readonly OperatorSessionRegistry _registry;

    public TerminalListSessionsHandler(OperatorSessionRegistry registry)
    {
        _registry = registry;
    }

    public ValueTask<TerminalListSessionsResponse?> HandleAsync(
        TerminalListSessionsRequest request,
        BridgeRequestContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var sessions = _registry.List(request.Kind, request.Backend, request.Status);
        var summaries = sessions.Select(TerminalSessionSummaryProjection.FromSession).ToList();

        return ValueTask.FromResult<TerminalListSessionsResponse?>(new TerminalListSessionsResponse
        {
            Sessions = summaries,
            Count = summaries.Count,
        });
    }

}

/// <summary>
/// Bridge command handler for reading bounded activity from an OperatorSession.
/// Works for observer-only Pi artifact sessions too (R907-4).
/// </summary>
public sealed class TerminalReadActivityHandler
    : IBridgeCommandHandler<TerminalReadActivityRequest, TerminalReadActivityResponse>
{
    private readonly OperatorSessionRegistry _registry;

    public TerminalReadActivityHandler(OperatorSessionRegistry registry)
    {
        _registry = registry;
    }

    public ValueTask<TerminalReadActivityResponse?> HandleAsync(
        TerminalReadActivityRequest request,
        BridgeRequestContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var session = _registry.Get(request.SessionId);
        if (session is null)
        {
            throw new BridgeHandlerException(
                "terminal.session.not_found",
                $"Session '{request.SessionId}' not found in local registry.",
                "not_found");
        }

        if (!session.Capabilities.CanReadActivity)
        {
            throw new BridgeHandlerException(
                "terminal.read_activity.unsupported",
                "Session does not support reading structured activity.",
                "unsupported_capability");
        }

        return ValueTask.FromResult<TerminalReadActivityResponse?>(
            OperatorSessionActivityReader.Read(session, request.AfterCursor, request.Limit));
    }
}

public sealed class TerminalAttachHandler
    : IBridgeCommandHandler<TerminalAttachRequest, TerminalAttachResponse>
{
    private readonly TerminalOperatorSessionService _terminals;

    public TerminalAttachHandler(TerminalOperatorSessionService terminals) => _terminals = terminals;

    public async ValueTask<TerminalAttachResponse?> HandleAsync(
        TerminalAttachRequest request,
        BridgeRequestContext context,
        CancellationToken cancellationToken)
    {
        return await _terminals.AttachAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class TerminalDetachHandler
    : IBridgeCommandHandler<TerminalDetachRequest, TerminalDetachResponse>
{
    private readonly TerminalOperatorSessionService _terminals;

    public TerminalDetachHandler(TerminalOperatorSessionService terminals) => _terminals = terminals;

    public async ValueTask<TerminalDetachResponse?> HandleAsync(
        TerminalDetachRequest request,
        BridgeRequestContext context,
        CancellationToken cancellationToken)
    {
        return await _terminals.DetachAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class TerminalSendInputHandler
    : IBridgeCommandHandler<TerminalSendInputRequest, TerminalSendInputResponse>
{
    private readonly TerminalOperatorSessionService _terminals;

    public TerminalSendInputHandler(TerminalOperatorSessionService terminals) => _terminals = terminals;

    public async ValueTask<TerminalSendInputResponse?> HandleAsync(
        TerminalSendInputRequest request,
        BridgeRequestContext context,
        CancellationToken cancellationToken)
    {
        return await _terminals.SendInputAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class TerminalResizeHandler
    : IBridgeCommandHandler<TerminalResizeRequest, TerminalResizeResponse>
{
    private readonly TerminalOperatorSessionService _terminals;

    public TerminalResizeHandler(TerminalOperatorSessionService terminals) => _terminals = terminals;

    public async ValueTask<TerminalResizeResponse?> HandleAsync(
        TerminalResizeRequest request,
        BridgeRequestContext context,
        CancellationToken cancellationToken)
    {
        return await _terminals.ResizeAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class TerminalTerminateHandler
    : IBridgeCommandHandler<TerminalTerminateRequest, TerminalTerminateResponse>
{
    private readonly TerminalOperatorSessionService _terminals;

    public TerminalTerminateHandler(TerminalOperatorSessionService terminals) => _terminals = terminals;

    public async ValueTask<TerminalTerminateResponse?> HandleAsync(
        TerminalTerminateRequest request,
        BridgeRequestContext context,
        CancellationToken cancellationToken)
    {
        return await _terminals.TerminateAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class TerminalReconnectHandler
    : IBridgeCommandHandler<TerminalReconnectRequest, TerminalAttachResponse>
{
    private readonly TerminalOperatorSessionService _terminals;

    public TerminalReconnectHandler(TerminalOperatorSessionService terminals) => _terminals = terminals;

    public async ValueTask<TerminalAttachResponse?> HandleAsync(
        TerminalReconnectRequest request,
        BridgeRequestContext context,
        CancellationToken cancellationToken)
    {
        return await _terminals.ReconnectAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class TerminalAckOutputHandler
    : IBridgeCommandHandler<TerminalAckOutputRequest, TerminalAckOutputResponse>
{
    private readonly TerminalOperatorSessionService _terminals;

    public TerminalAckOutputHandler(TerminalOperatorSessionService terminals) => _terminals = terminals;

    public async ValueTask<TerminalAckOutputResponse?> HandleAsync(
        TerminalAckOutputRequest request,
        BridgeRequestContext context,
        CancellationToken cancellationToken)
    {
        return await _terminals.AckOutputAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
