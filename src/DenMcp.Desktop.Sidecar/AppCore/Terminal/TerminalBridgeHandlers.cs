using Den.Bridge.Abstractions;
using Den.Bridge.Protocol;

namespace DenMcp.Desktop.Sidecar;

/// <summary>
/// Bridge command handler for creating tmux-backed OperatorSessions.
/// </summary>
public sealed class TerminalCreateSessionHandler
    : IBridgeCommandHandler<TerminalCreateSessionRequest, TerminalCreateSessionResponse>
{
    private readonly TmuxOperatorSessionService _tmux;

    public TerminalCreateSessionHandler(TmuxOperatorSessionService tmux)
    {
        _tmux = tmux;
    }

    public async ValueTask<TerminalCreateSessionResponse?> HandleAsync(
        TerminalCreateSessionRequest request,
        BridgeRequestContext context,
        CancellationToken cancellationToken)
    {
        var session = await _tmux.CreateAsync(request, cancellationToken).ConfigureAwait(false);
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

        // Bounded activity from the session's recent activity list.
        var afterCursor = request.AfterCursor;
        var limit = Math.Clamp(request.Limit, 1, 200);
        var allItems = session.RecentActivity;
        var startIndex = 0;

        if (afterCursor is not null && long.TryParse(afterCursor.Replace("cur_", ""), out var afterSeq))
        {
            startIndex = (int)Math.Min(afterSeq, allItems.Count);
        }

        var items = allItems.Skip(startIndex).Take(limit).Select(a => new TerminalActivityItem
        {
            Kind = a.Kind,
            Role = a.Role,
            Tool = a.Tool,
            Summary = a.Summary,
            Timestamp = a.Timestamp,
        }).ToList();

        var nextCursor = items.Count == limit ? $"cur_{startIndex + limit:D12}" : null;
        var truncated = items.Count < allItems.Count - startIndex;

        return ValueTask.FromResult<TerminalReadActivityResponse?>(new TerminalReadActivityResponse
        {
            SessionId = request.SessionId,
            Items = items,
            NextCursor = nextCursor,
            Truncated = truncated,
        });
    }
}

public sealed class TerminalAttachHandler
    : IBridgeCommandHandler<TerminalAttachRequest, TerminalAttachResponse>
{
    private readonly TmuxOperatorSessionService _tmux;

    public TerminalAttachHandler(TmuxOperatorSessionService tmux) => _tmux = tmux;

    public async ValueTask<TerminalAttachResponse?> HandleAsync(
        TerminalAttachRequest request,
        BridgeRequestContext context,
        CancellationToken cancellationToken)
    {
        return await _tmux.AttachAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class TerminalDetachHandler
    : IBridgeCommandHandler<TerminalDetachRequest, TerminalDetachResponse>
{
    private readonly TmuxOperatorSessionService _tmux;

    public TerminalDetachHandler(TmuxOperatorSessionService tmux) => _tmux = tmux;

    public async ValueTask<TerminalDetachResponse?> HandleAsync(
        TerminalDetachRequest request,
        BridgeRequestContext context,
        CancellationToken cancellationToken)
    {
        return await _tmux.DetachAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class TerminalSendInputHandler
    : IBridgeCommandHandler<TerminalSendInputRequest, TerminalSendInputResponse>
{
    private readonly TmuxOperatorSessionService _tmux;

    public TerminalSendInputHandler(TmuxOperatorSessionService tmux) => _tmux = tmux;

    public async ValueTask<TerminalSendInputResponse?> HandleAsync(
        TerminalSendInputRequest request,
        BridgeRequestContext context,
        CancellationToken cancellationToken)
    {
        return await _tmux.SendInputAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class TerminalResizeHandler
    : IBridgeCommandHandler<TerminalResizeRequest, TerminalResizeResponse>
{
    private readonly TmuxOperatorSessionService _tmux;

    public TerminalResizeHandler(TmuxOperatorSessionService tmux) => _tmux = tmux;

    public async ValueTask<TerminalResizeResponse?> HandleAsync(
        TerminalResizeRequest request,
        BridgeRequestContext context,
        CancellationToken cancellationToken)
    {
        return await _tmux.ResizeAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class TerminalTerminateHandler
    : IBridgeCommandHandler<TerminalTerminateRequest, TerminalTerminateResponse>
{
    private readonly TmuxOperatorSessionService _tmux;

    public TerminalTerminateHandler(TmuxOperatorSessionService tmux) => _tmux = tmux;

    public async ValueTask<TerminalTerminateResponse?> HandleAsync(
        TerminalTerminateRequest request,
        BridgeRequestContext context,
        CancellationToken cancellationToken)
    {
        return await _tmux.TerminateAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class TerminalReconnectHandler
    : IBridgeCommandHandler<TerminalReconnectRequest, TerminalAttachResponse>
{
    private readonly TmuxOperatorSessionService _tmux;

    public TerminalReconnectHandler(TmuxOperatorSessionService tmux) => _tmux = tmux;

    public async ValueTask<TerminalAttachResponse?> HandleAsync(
        TerminalReconnectRequest request,
        BridgeRequestContext context,
        CancellationToken cancellationToken)
    {
        return await _tmux.ReconnectAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class TerminalAckOutputHandler
    : IBridgeCommandHandler<TerminalAckOutputRequest, TerminalAckOutputResponse>
{
    private readonly TmuxOperatorSessionService _tmux;

    public TerminalAckOutputHandler(TmuxOperatorSessionService tmux) => _tmux = tmux;

    public async ValueTask<TerminalAckOutputResponse?> HandleAsync(
        TerminalAckOutputRequest request,
        BridgeRequestContext context,
        CancellationToken cancellationToken)
    {
        return await _tmux.AckOutputAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
