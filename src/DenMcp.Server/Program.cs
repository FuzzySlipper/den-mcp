using System.Text.Json;
using System.Text.Json.Serialization;
using DenMcp.Core;
using DenMcp.Core.Data;
using DenMcp.Core.Llm;
using DenMcp.Core.Models;
using DenMcp.Core.Services;
using DenMcp.Server;
using DenMcp.Server.CoreClient;
using DenMcp.Server.Realtime;
using DenMcp.Server.Routes;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

var builder = WebApplication.CreateBuilder(args);

// Configuration (appsettings.json + environment variables + CLI args)
var options = new DenMcpOptions();
PreparePiSessionHostArraysForBinding(options.PiSessionHost);
builder.Configuration.GetSection("DenMcp").Bind(options);
ApplyPiSessionHostArrayDefaults(options.PiSessionHost);

// CLI overrides: --port and --db-path
if (builder.Configuration["port"] is { } port)
    options.ListenUrl = $"http://localhost:{port}";
if (builder.Configuration["db-path"] is { } dbPathOverride)
    options.DatabasePath = dbPathOverride;

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(options.PiSessionHost);
var denCoreOptions = new DenCoreOptions();
builder.Configuration.GetSection("DenCore").Bind(denCoreOptions);
if (builder.Configuration["den-core-url"] is { } denCoreUrl)
    denCoreOptions.BaseUrl = denCoreUrl;
if (builder.Configuration["den-core-timeout-seconds"] is { } denCoreTimeout &&
    int.TryParse(denCoreTimeout, out var parsedDenCoreTimeout))
    denCoreOptions.TimeoutSeconds = parsedDenCoreTimeout;
builder.Services.AddSingleton(denCoreOptions);
builder.Services.AddHttpClient<DenCoreClient>();
builder.Services.AddHttpClient("DenCoreMcpProxy");
var trustedPublisherOptions = new TrustedPublisherOptions();
builder.Configuration.GetSection("DenMcp:TrustedPublisher").Bind(trustedPublisherOptions);
builder.Services.AddSingleton(trustedPublisherOptions);

// LLM (librarian)
var llmConfig = new LlmConfig();
builder.Configuration.GetSection("DenMcp:Llm").Bind(llmConfig);
if (builder.Configuration["llm-endpoint"] is { } llmEndpoint)
    llmConfig.Endpoint = llmEndpoint;
if (builder.Configuration["llm-api-key"] is { } llmApiKey)
    llmConfig.ApiKey = llmApiKey;
if (builder.Configuration["llm-model"] is { } llmModel)
    llmConfig.Model = llmModel;
if (builder.Configuration["llm-max-tokens"] is { } llmMaxTokens &&
    int.TryParse(llmMaxTokens, out var parsedMaxTokens))
    llmConfig.MaxTokens = parsedMaxTokens;
if (builder.Configuration["llm-context-token-budget"] is { } llmContextTokenBudget &&
    int.TryParse(llmContextTokenBudget, out var parsedContextTokenBudget))
    llmConfig.ContextTokenBudget = parsedContextTokenBudget;
builder.Services.AddSingleton(llmConfig);
builder.Services.AddSingleton<ILlmClient, OpenAiCompatibleLlmClient>();

// Kestrel
builder.WebHost.UseUrls(options.ListenUrl);

// JSON serialization
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

// Database-backed legacy services. In deployed MCP-adapter mode, Den Core is the
// sole SQLite owner/writer and converted MCP tools call Core over HTTP instead.
DatabaseInitializer? initializer = null;
if (options.LocalDatabaseEnabled)
{
    var dbPath = options.GetResolvedDatabasePath();
    initializer = new DatabaseInitializer(dbPath, NullLogger<DatabaseInitializer>.Instance);
    builder.Services.AddSingleton(new DbConnectionFactory(initializer.ConnectionString));

    // Repositories
    builder.Services.AddSingleton<IProjectRepository, ProjectRepository>();
    builder.Services.AddSingleton<ITopicRepository, TopicRepository>();
    builder.Services.AddSingleton<ITopicClipQueueRepository, TopicClipQueueRepository>();
    builder.Services.AddSingleton<ITaskRepository, TaskRepository>();
    builder.Services.AddSingleton<IReviewRoundRepository, ReviewRoundRepository>();
    builder.Services.AddSingleton<IReviewFindingRepository, ReviewFindingRepository>();
    builder.Services.AddSingleton<IMessageRepository, MessageRepository>();
    builder.Services.AddSingleton<IDocumentRepository, DocumentRepository>();
    builder.Services.AddSingleton<IBlackboardRepository, BlackboardRepository>();
    builder.Services.AddSingleton<IAgentGuidanceRepository, AgentGuidanceRepository>();
    builder.Services.AddSingleton<IAgentSessionRepository, AgentSessionRepository>();
    builder.Services.AddSingleton<IAgentInstanceBindingRepository, AgentInstanceBindingRepository>();
    builder.Services.AddSingleton<DispatchRepository>();
    builder.Services.AddSingleton<IAgentStreamRepository, AgentStreamRepository>();
    builder.Services.AddSingleton<IAgentRunRepository, AgentRunRepository>();
    builder.Services.AddSingleton<IAgentWorkspaceRepository, AgentWorkspaceRepository>();
    builder.Services.AddSingleton<IPiSessionRepository, PiSessionRepository>();
    builder.Services.AddSingleton<IDesktopSnapshotRepository, DesktopSnapshotRepository>();
    builder.Services.AddSingleton<IDesktopSessionEventRepository, DesktopSessionEventRepository>();
    builder.Services.AddSingleton<ICollaborationRepository, CollaborationRepository>();
    builder.Services.AddSingleton<AgentStreamRealtimeHub>();
    builder.Services.AddSingleton<INotificationChannel, NoOpNotificationChannel>();
    builder.Services.AddSingleton<IAgentStreamOpsService, AgentStreamOpsService>();
    builder.Services.AddSingleton<IDispatchRepository>(services =>
        new AgentStreamDispatchRepository(
            services.GetRequiredService<DispatchRepository>(),
            services.GetRequiredService<IAgentStreamOpsService>()));
    builder.Services.AddSingleton<IReviewWorkflowService, ReviewWorkflowService>();
    builder.Services.AddSingleton<IReviewFindingTriageService, ReviewFindingTriageService>();
    builder.Services.AddSingleton<IAgentRecipientResolver, AgentRecipientResolver>();
    builder.Services.AddSingleton<IAgentStreamMessageService, AgentStreamMessageService>();
    builder.Services.AddSingleton<ISubagentRunService, SubagentRunService>();
    builder.Services.AddSingleton<IAttentionService, AttentionService>();
    builder.Services.AddSingleton<IGitInspectionService, GitInspectionService>();
    builder.Services.AddSingleton<ITrustedPublisherService, TrustedPublisherService>();
    builder.Services.AddSingleton<IPiDockerLaunchProfileRenderer, PiDockerLaunchProfileRenderer>();
    builder.Services.AddSingleton<IProcessRunner, SystemProcessRunner>();
    builder.Services.AddSingleton<IPiSessionHost, TmuxDockerPiSessionHost>();
    builder.Services.AddSingleton<IPiSessionService, PiSessionService>();

    // Dispatch
    builder.Services.AddSingleton<IRoutingService, RoutingService>();
    builder.Services.AddSingleton<IPromptGenerationService, PromptGenerationService>();
    builder.Services.AddSingleton<IDispatchContextService, DispatchContextService>();
    builder.Services.AddSingleton<IDispatchDetectionService, DispatchDetectionService>();

    // Librarian
    builder.Services.AddSingleton<LibrarianGatherer>();
    builder.Services.AddSingleton<LibrarianService>();
}

// MCP. Legacy/local mode hosts tools in-process. Adapter mode proxies the
// stable public /mcp endpoint to Den Core so the full tool surface stays
// available while den-mcp itself does not open canonical SQLite.
if (options.LocalDatabaseEnabled)
{
    builder.Services.AddMcpServer()
        .WithHttpTransport()
        .WithToolsFromAssembly();
}

var app = builder.Build();

// Initialize database on startup
if (initializer is not null)
    await initializer.InitializeAsync();

// Static files (web frontend)
app.UseDefaultFiles();
app.UseStaticFiles();

// Health check
app.MapGet("/health", async (DenCoreClient coreClient) =>
{
    object coreStatus;
    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        coreStatus = new
        {
            status = "healthy",
            response = await coreClient.GetHealthAsync(cts.Token)
        };
    }
    catch (DenCoreException ex)
    {
        coreStatus = DenCoreToolError.FromException(ex);
    }

    return Results.Ok(new
    {
        status = "healthy",
        adapter = new
        {
            status = "healthy",
            version = BuildInfo.Version,
            informationalVersion = BuildInfo.InformationalVersion,
            commit = BuildInfo.Commit
        },
        denCore = coreStatus
    });
});

// REST API. These routes are legacy DB-backed surfaces; in adapter mode they are
// intentionally not mapped so den-mcp can run without SQLite access.
if (options.LocalDatabaseEnabled)
{
    app.MapProjectRoutes();
    app.MapSpaceRoutes();
    app.MapTopicRoutes();
    app.MapTopicClipQueueRoutes();
    app.MapTaskRoutes();
    app.MapMessageRoutes();
    app.MapDocumentRoutes();
    app.MapBlackboardRoutes();
    app.MapAgentGuidanceRoutes();
    app.MapAgentRoutes();
    app.MapDispatchRoutes();
    app.MapAgentStreamRoutes();
    app.MapSubagentRunRoutes();
    app.MapAgentWorkspaceRoutes();
    app.MapDesktopSnapshotRoutes();
    app.MapDesktopSessionEventRoutes();
    app.MapCollaborationRoutes();
    app.MapAttentionRoutes();
    app.MapGitInspectionRoutes();
    app.MapPiLaunchProfileRoutes();
    app.MapPiSessionRoutes();
    app.MapLibrarianRoutes();
}

// MCP endpoint
if (options.LocalDatabaseEnabled)
{
    app.MapMcp("/mcp");
}
else
{
    MapDenCoreMcpProxy(app);
}

// SPA fallback — serves index.html for unmatched routes
app.MapFallbackToFile("index.html");

static void MapDenCoreMcpProxy(WebApplication app)
{
    var methods = new[] { "GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS" };
    app.MapMethods("/mcp", methods, ProxyMcpToDenCoreAsync);
    app.MapMethods("/mcp/{**path}", methods, ProxyMcpToDenCoreAsync);
}

static async Task ProxyMcpToDenCoreAsync(HttpContext context, IHttpClientFactory httpClientFactory, DenCoreOptions coreOptions)
{
    using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), BuildDenCoreMcpUri(coreOptions, context.Request));

    if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
    {
        request.Content = new StreamContent(context.Request.Body);
        foreach (var header in context.Request.Headers)
        {
            if (!header.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                continue;
            request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }
    }

    foreach (var header in context.Request.Headers)
    {
        if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
            header.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
            continue;
        request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
    }

    var client = httpClientFactory.CreateClient("DenCoreMcpProxy");
    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);

    context.Response.StatusCode = (int)response.StatusCode;
    foreach (var header in response.Headers)
        context.Response.Headers[header.Key] = header.Value.ToArray();
    foreach (var header in response.Content.Headers)
        context.Response.Headers[header.Key] = header.Value.ToArray();
    context.Response.Headers.Remove("transfer-encoding");

    await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
}

static Uri BuildDenCoreMcpUri(DenCoreOptions coreOptions, HttpRequest request)
{
    var baseUrl = string.IsNullOrWhiteSpace(coreOptions.BaseUrl)
        ? "http://localhost:5299"
        : coreOptions.BaseUrl.TrimEnd('/');
    return new Uri(baseUrl + request.Path + request.QueryString);
}

static void PreparePiSessionHostArraysForBinding(PiDockerLaunchProfileOptions options)
{
    // Microsoft.Extensions.Configuration binds array values by extending existing
    // initialized collections. Clear array defaults before binding so configured
    // arrays replace defaults instead of appending to them.
    options.TmuxShellCommand = [];
    options.ProviderSecretEnvironmentVariables = [];
    options.RequiredPiStatePaths = [];
}

static void ApplyPiSessionHostArrayDefaults(PiDockerLaunchProfileOptions options)
{
    if (options.TmuxShellCommand.Length == 0)
        options.TmuxShellCommand = PiDockerLaunchProfileDefaults.TmuxShellCommand.ToArray();
    if (options.ProviderSecretEnvironmentVariables.Length == 0)
        options.ProviderSecretEnvironmentVariables = PiDockerLaunchProfileDefaults.ProviderSecretEnvironmentVariables.ToArray();
    if (options.RequiredPiStatePaths.Length == 0)
        options.RequiredPiStatePaths = ["agent/settings.json"];
}

app.Run();

public partial class Program;
