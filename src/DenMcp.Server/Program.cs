using System.Text.Json;
using System.Text.Json.Serialization;
using DenMcp.Core;
using DenMcp.Core.Data;
using DenMcp.Core.Llm;
using DenMcp.Core.Models;
using DenMcp.Core.Services;
using DenMcp.Server;
using DenMcp.Server.Notifications;
using DenMcp.Server.Routes;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

var builder = WebApplication.CreateBuilder(args);

// Configuration (appsettings.json + environment variables + CLI args)
var options = new DenMcpOptions();
builder.Configuration.GetSection("DenMcp").Bind(options);

// CLI overrides: --port and --db-path
if (builder.Configuration["port"] is { } port)
    options.ListenUrl = $"http://localhost:{port}";
if (builder.Configuration["db-path"] is { } dbPathOverride)
    options.DatabasePath = dbPathOverride;

builder.Services.AddSingleton(options);

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

// Database
var dbPath = options.GetResolvedDatabasePath();
var initializer = new DatabaseInitializer(dbPath, NullLogger<DatabaseInitializer>.Instance);
builder.Services.AddSingleton(new DbConnectionFactory(initializer.ConnectionString));

// Repositories
builder.Services.AddSingleton<IProjectRepository, ProjectRepository>();
builder.Services.AddSingleton<ITaskRepository, TaskRepository>();
builder.Services.AddSingleton<IReviewRoundRepository, ReviewRoundRepository>();
builder.Services.AddSingleton<IReviewFindingRepository, ReviewFindingRepository>();
builder.Services.AddSingleton<IMessageRepository, MessageRepository>();
builder.Services.AddSingleton<IDocumentRepository, DocumentRepository>();
builder.Services.AddSingleton<IAgentGuidanceRepository, AgentGuidanceRepository>();
builder.Services.AddSingleton<IAgentSessionRepository, AgentSessionRepository>();
builder.Services.AddSingleton<IAgentInstanceBindingRepository, AgentInstanceBindingRepository>();
builder.Services.AddSingleton<DispatchRepository>();
builder.Services.AddSingleton<IAgentStreamRepository, AgentStreamRepository>();
builder.Services.AddSingleton<INotificationMessageRepository, NotificationMessageRepository>();
builder.Services.AddSingleton<IAgentStreamOpsService, AgentStreamOpsService>();
builder.Services.AddSingleton<IDispatchRepository>(services =>
    new AgentStreamDispatchRepository(
        services.GetRequiredService<DispatchRepository>(),
        services.GetRequiredService<IAgentStreamOpsService>()));
builder.Services.AddSingleton<IReviewWorkflowService, ReviewWorkflowService>();
builder.Services.AddSingleton<IAgentRecipientResolver, AgentRecipientResolver>();
builder.Services.AddSingleton<IAgentStreamMessageService, AgentStreamMessageService>();

// Dispatch
builder.Services.AddSingleton<IRoutingService, RoutingService>();
builder.Services.AddSingleton<IPromptGenerationService, PromptGenerationService>();
builder.Services.AddSingleton<IDispatchContextService, DispatchContextService>();
builder.Services.AddSingleton<IDispatchDetectionService, DispatchDetectionService>();
builder.Services.AddHttpClient("signal-daemon", (services, client) =>
{
    var denOptions = services.GetRequiredService<DenMcpOptions>();
    client.BaseAddress = new Uri(denOptions.Signal.GetBaseUrl());
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddHttpClient("signal-events", (services, client) =>
{
    var denOptions = services.GetRequiredService<DenMcpOptions>();
    client.BaseAddress = new Uri(denOptions.Signal.GetBaseUrl());
    client.Timeout = Timeout.InfiniteTimeSpan;
});
builder.Services.AddSingleton<SignalNotificationChannel>();
builder.Services.AddSingleton<INotificationChannel>(services => services.GetRequiredService<SignalNotificationChannel>());
builder.Services.AddHostedService<NotificationListenerHostedService>();

// Librarian
builder.Services.AddSingleton<LibrarianGatherer>();
builder.Services.AddSingleton<LibrarianService>();

// MCP
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

// Initialize database on startup
await initializer.InitializeAsync();

// Static files (web frontend)
app.UseDefaultFiles();
app.UseStaticFiles();

// Health check
app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    version = BuildInfo.Version,
    informationalVersion = BuildInfo.InformationalVersion,
    commit = BuildInfo.Commit
}));

// REST API
app.MapProjectRoutes();
app.MapTaskRoutes();
app.MapMessageRoutes();
app.MapDocumentRoutes();
app.MapAgentGuidanceRoutes();
app.MapAgentRoutes();
app.MapDispatchRoutes();
app.MapAgentStreamRoutes();
app.MapLibrarianRoutes();

// MCP endpoint
app.MapMcp("/mcp");

// SPA fallback — serves index.html for unmatched routes
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
