using Den.Bridge.Abstractions;
using Den.Bridge.Protocol;

namespace DenMcp.Desktop.Sidecar;

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
        var summaries = sessions.Select(s => new TerminalSessionSummary
        {
            SessionId = s.SessionId,
            Title = s.Title,
            DisplayName = s.DisplayName,
            Kind = s.Kind,
            Backend = s.Backend,
            Status = s.Status,
            CurrentCommand = s.CurrentCommand,
            AgentIdentity = s.AgentIdentity,
            Role = s.Role,
            ProjectId = s.ProjectId,
            TaskId = s.TaskId,
            WorkspaceId = s.WorkspaceId,
            CanReadActivity = s.Capabilities.CanReadActivity,
            CanSendInput = s.Capabilities.CanSendInput,
            CanTerminate = s.Capabilities.CanTerminate,
            CanAttach = s.Capabilities.CanAttach,
            CreatedAt = FormatDateTime(s.CreatedAt),
            LastActivityAt = FormatDateTime(s.LastActivityAt),
            ExitedAt = FormatDateTime(s.ExitedAt),
            ExitCode = s.ExitCode,
        }).ToList();

        return ValueTask.FromResult<TerminalListSessionsResponse?>(new TerminalListSessionsResponse
        {
            Sessions = summaries,
            Count = summaries.Count,
        });
    }

    private static string? FormatDateTime(DateTime? dt) => dt?.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
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

/// <summary>
/// Stub handler for terminal.attach — returns unsupported capability error
/// until a terminal backend is implemented (#909/#911).
/// </summary>
public sealed class TerminalAttachHandler
    : IBridgeCommandHandler<TerminalAttachRequest, TerminalAttachResponse>
{
    public ValueTask<TerminalAttachResponse?> HandleAsync(
        TerminalAttachRequest request,
        BridgeRequestContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new BridgeHandlerException(
            "terminal.attach.unsupported",
            "Terminal attach is not yet supported. A terminal backend (direct PTY, tmux) must be implemented first.",
            "unsupported_capability");
    }
}

/// <summary>
/// Stub handler for terminal.detach — returns unsupported capability error.
/// </summary>
public sealed class TerminalDetachHandler
    : IBridgeCommandHandler<TerminalDetachRequest, TerminalDetachResponse>
{
    public ValueTask<TerminalDetachResponse?> HandleAsync(
        TerminalDetachRequest request,
        BridgeRequestContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new BridgeHandlerException(
            "terminal.detach.unsupported",
            "Terminal detach is not yet supported.",
            "unsupported_capability");
    }
}

/// <summary>
/// Stub handler for terminal.send_input — returns unsupported capability error.
/// </summary>
public sealed class TerminalSendInputHandler
    : IBridgeCommandHandler<TerminalSendInputRequest, TerminalSendInputResponse>
{
    public ValueTask<TerminalSendInputResponse?> HandleAsync(
        TerminalSendInputRequest request,
        BridgeRequestContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new BridgeHandlerException(
            "terminal.send_input.unsupported",
            "Terminal input is not yet supported.",
            "unsupported_capability");
    }
}

/// <summary>
/// Stub handler for terminal.resize — returns unsupported capability error.
/// </summary>
public sealed class TerminalResizeHandler
    : IBridgeCommandHandler<TerminalResizeRequest, TerminalResizeResponse>
{
    public ValueTask<TerminalResizeResponse?> HandleAsync(
        TerminalResizeRequest request,
        BridgeRequestContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new BridgeHandlerException(
            "terminal.resize.unsupported",
            "Terminal resize is not yet supported.",
            "unsupported_capability");
    }
}

/// <summary>
/// Stub handler for terminal.terminate — returns unsupported capability error.
/// </summary>
public sealed class TerminalTerminateHandler
    : IBridgeCommandHandler<TerminalTerminateRequest, TerminalTerminateResponse>
{
    public ValueTask<TerminalTerminateResponse?> HandleAsync(
        TerminalTerminateRequest request,
        BridgeRequestContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new BridgeHandlerException(
            "terminal.terminate.unsupported",
            "Terminal terminate is not yet supported. A terminal backend must be implemented first.",
            "unsupported_capability");
    }
}

/// <summary>
/// Stub handler for terminal.reconnect — returns unsupported capability error.
/// </summary>
public sealed class TerminalReconnectHandler
    : IBridgeCommandHandler<TerminalReconnectRequest, TerminalAttachResponse>
{
    public ValueTask<TerminalAttachResponse?> HandleAsync(
        TerminalReconnectRequest request,
        BridgeRequestContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new BridgeHandlerException(
            "terminal.reconnect.unsupported",
            "Terminal reconnect is not yet supported.",
            "unsupported_capability");
    }
}

/// <summary>
/// Stub handler for terminal.ack_output — returns unsupported capability error.
/// </summary>
public sealed class TerminalAckOutputHandler
    : IBridgeCommandHandler<TerminalAckOutputRequest, TerminalAckOutputResponse>
{
    public ValueTask<TerminalAckOutputResponse?> HandleAsync(
        TerminalAckOutputRequest request,
        BridgeRequestContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new BridgeHandlerException(
            "terminal.ack_output.unsupported",
            "Terminal ack_output is not yet supported.",
            "unsupported_capability");
    }
}
