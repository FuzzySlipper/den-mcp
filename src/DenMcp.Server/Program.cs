using System.Text.Json;
using System.Text.Json.Serialization;
using DenMcp.Core.Data;
using DenMcp.Core.Llm;
using DenMcp.Core.Models;
using DenMcp.Server;
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
builder.Services.AddSingleton<IMessageRepository, MessageRepository>();
builder.Services.AddSingleton<IDocumentRepository, DocumentRepository>();
builder.Services.AddSingleton<IAgentSessionRepository, AgentSessionRepository>();

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
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

// REST API
app.MapProjectRoutes();
app.MapTaskRoutes();
app.MapMessageRoutes();
app.MapDocumentRoutes();
app.MapAgentRoutes();

// MCP endpoint
app.MapMcp("/mcp");

// SPA fallback — serves index.html for unmatched routes
app.MapFallbackToFile("index.html");

app.Run();
