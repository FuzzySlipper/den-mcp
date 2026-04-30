using Den.Bridge.Abstractions;
using Den.Bridge.Protocol;

namespace DenMcp.Desktop.Sidecar;

public sealed class TerminalOperatorSessionService
{
    private readonly OperatorSessionRegistry _registry;
    private readonly TmuxOperatorSessionService _tmux;
    private readonly DirectPtyOperatorSessionService _directPty;

    public TerminalOperatorSessionService(
        OperatorSessionRegistry registry,
        TmuxOperatorSessionService tmux,
        DirectPtyOperatorSessionService directPty)
    {
        _registry = registry;
        _tmux = tmux;
        _directPty = directPty;
    }

    public Task<OperatorSession> CreateAsync(TerminalCreateSessionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Backend switch
        {
            OperatorSessionBackend.DirectPty => _directPty.CreateAsync(request, cancellationToken),
            OperatorSessionBackend.Tmux => _tmux.CreateAsync(request, cancellationToken),
            _ => throw Unsupported("create_session", $"Unknown terminal backend '{request.Backend}'."),
        };
    }

    public Task<IReadOnlyList<OperatorSession>> RediscoverAsync(CancellationToken cancellationToken = default)
    {
        return _tmux.RediscoverAsync(cancellationToken);
    }

    public TerminalAttachResponse BuildUnsupportedAttachInfo(OperatorSession session)
    {
        return new TerminalAttachResponse
        {
            StreamId = string.Empty,
            SessionId = session.SessionId,
            AttachedAt = string.Empty,
            StartCursor = string.Empty,
            ReplayAvailableFrom = string.Empty,
            Capabilities = new TerminalAttachCapabilities(),
            Limits = new TerminalStreamLimits(),
        };
    }

    public Task<TerminalAttachResponse> AttachAsync(TerminalAttachRequest request, CancellationToken cancellationToken = default)
    {
        return Route(request.SessionId, "attach", s => s.Backend switch
        {
            OperatorSessionBackend.DirectPty => _directPty.AttachAsync(request, cancellationToken),
            OperatorSessionBackend.Tmux => _tmux.AttachAsync(request, cancellationToken),
            _ => throw Unsupported("attach", "Session backend does not support terminal attach."),
        });
    }

    public Task<TerminalDetachResponse> DetachAsync(TerminalDetachRequest request, CancellationToken cancellationToken = default)
    {
        return Route(request.SessionId, "detach", s => s.Backend switch
        {
            OperatorSessionBackend.DirectPty => _directPty.DetachAsync(request, cancellationToken),
            OperatorSessionBackend.Tmux => _tmux.DetachAsync(request, cancellationToken),
            _ => throw Unsupported("detach", "Session backend does not support terminal detach."),
        });
    }

    public Task<TerminalSendInputResponse> SendInputAsync(TerminalSendInputRequest request, CancellationToken cancellationToken = default)
    {
        return Route(request.SessionId, "send_input", s => s.Backend switch
        {
            OperatorSessionBackend.DirectPty => _directPty.SendInputAsync(request, cancellationToken),
            OperatorSessionBackend.Tmux => _tmux.SendInputAsync(request, cancellationToken),
            _ => throw Unsupported("send_input", "Session backend does not support terminal input."),
        });
    }

    public Task<TerminalResizeResponse> ResizeAsync(TerminalResizeRequest request, CancellationToken cancellationToken = default)
    {
        return Route(request.SessionId, "resize", s => s.Backend switch
        {
            OperatorSessionBackend.DirectPty => _directPty.ResizeAsync(request, cancellationToken),
            OperatorSessionBackend.Tmux => _tmux.ResizeAsync(request, cancellationToken),
            _ => throw Unsupported("resize", "Session backend does not support terminal resize."),
        });
    }

    public Task<TerminalTerminateResponse> TerminateAsync(TerminalTerminateRequest request, CancellationToken cancellationToken = default)
    {
        return Route(request.SessionId, "terminate", s => s.Backend switch
        {
            OperatorSessionBackend.DirectPty => _directPty.TerminateAsync(request, cancellationToken),
            OperatorSessionBackend.Tmux => _tmux.TerminateAsync(request, cancellationToken),
            _ => throw Unsupported("terminate", "Session backend does not support terminal terminate."),
        });
    }

    public Task<TerminalAttachResponse> ReconnectAsync(TerminalReconnectRequest request, CancellationToken cancellationToken = default)
    {
        return Route(request.SessionId, "reconnect", s => s.Backend switch
        {
            OperatorSessionBackend.DirectPty => _directPty.ReconnectAsync(request, cancellationToken),
            OperatorSessionBackend.Tmux => _tmux.ReconnectAsync(request, cancellationToken),
            _ => throw Unsupported("reconnect", "Session backend does not support terminal reconnect."),
        });
    }

    public Task<TerminalAckOutputResponse> AckOutputAsync(TerminalAckOutputRequest request, CancellationToken cancellationToken = default)
    {
        return Route(request.SessionId, "ack_output", s => s.Backend switch
        {
            OperatorSessionBackend.DirectPty => _directPty.AckOutputAsync(request, cancellationToken),
            OperatorSessionBackend.Tmux => _tmux.AckOutputAsync(request, cancellationToken),
            _ => throw Unsupported("ack_output", "Session backend does not support terminal output acknowledgement."),
        });
    }

    public IReadOnlyList<LocalSessionSnapshot> BuildSnapshotListForDen()
    {
        return _tmux.BuildSnapshotListForDen().Concat(_directPty.BuildSnapshotListForDen()).ToList();
    }

    private T Route<T>(string sessionId, string action, Func<OperatorSession, T> route)
    {
        var session = _registry.Get(sessionId);
        if (session is null)
        {
            throw new BridgeHandlerException("terminal.session.not_found", $"Session '{sessionId}' not found in local registry.", BridgeErrorCategories.NotFound);
        }

        return route(session);
    }

    private static BridgeHandlerException Unsupported(string action, string detail) => new($"terminal.{action}.unsupported", $"Terminal action '{action}' is not supported by this backend or session. {detail}", BridgeErrorCategories.UnsupportedCapability);
}
