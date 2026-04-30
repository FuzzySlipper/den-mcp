using Den.Bridge.Abstractions;

namespace DenMcp.Desktop.Sidecar;

public sealed class AppAgentBuildContextHandler
    : IBridgeCommandHandler<AppAgentBuildContextRequest, AppAgentBuildContextResponse>
{
    private readonly AppAgentService _appAgent;

    public AppAgentBuildContextHandler(AppAgentService appAgent) => _appAgent = appAgent;

    public async ValueTask<AppAgentBuildContextResponse?> HandleAsync(
        AppAgentBuildContextRequest request,
        BridgeRequestContext context,
        CancellationToken cancellationToken)
    {
        var effective = request with { ParentRequestId = request.ParentRequestId ?? context.RequestId };
        var packet = await _appAgent.BuildContextAsync(effective, cancellationToken).ConfigureAwait(false);
        return new AppAgentBuildContextResponse { Context = packet };
    }
}

public sealed class AppAgentListToolsHandler
    : IBridgeCommandHandler<AppAgentListToolsRequest, AppAgentListToolsResponse>
{
    private readonly AppAgentService _appAgent;

    public AppAgentListToolsHandler(AppAgentService appAgent) => _appAgent = appAgent;

    public ValueTask<AppAgentListToolsResponse?> HandleAsync(
        AppAgentListToolsRequest request,
        BridgeRequestContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<AppAgentListToolsResponse?>(new AppAgentListToolsResponse
        {
            Tools = _appAgent.ListTools(request.Selection),
        });
    }
}

public sealed class AppAgentInvokeToolHandler
    : IBridgeCommandHandler<AppAgentInvokeToolRequest, AppAgentInvokeToolResponse>
{
    private readonly AppAgentService _appAgent;

    public AppAgentInvokeToolHandler(AppAgentService appAgent) => _appAgent = appAgent;

    public async ValueTask<AppAgentInvokeToolResponse?> HandleAsync(
        AppAgentInvokeToolRequest request,
        BridgeRequestContext context,
        CancellationToken cancellationToken)
    {
        return await _appAgent.InvokeToolAsync(context.RequestId, request, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class AppAgentCancelRequestHandler
    : IBridgeCommandHandler<AppAgentCancelRequest, AppAgentCancelResponse>
{
    private readonly AppAgentService _appAgent;

    public AppAgentCancelRequestHandler(AppAgentService appAgent) => _appAgent = appAgent;

    public ValueTask<AppAgentCancelResponse?> HandleAsync(
        AppAgentCancelRequest request,
        BridgeRequestContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<AppAgentCancelResponse?>(_appAgent.Cancel(request));
    }
}
