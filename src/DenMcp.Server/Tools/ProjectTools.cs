using System.ComponentModel;
using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using ModelContextProtocol.Server;

namespace DenMcp.Server.Tools;

[McpServerToolType]
public sealed class ProjectTools
{
    [McpServerTool(Name = "create_project"), Description("Register a new project for task management, messaging, and document storage.")]
    public static async Task<string> CreateProject(
        IProjectRepository repo,
        [Description("Unique project ID slug, e.g. 'my-project'. Typically the directory name.")] string id,
        [Description("Human-readable display name.")] string name,
        [Description("Absolute path to the project root on disk.")] string? root_path = null,
        [Description("Short description of the project.")] string? description = null)
    {
        var project = await repo.CreateAsync(new Project
        {
            Id = id,
            Name = name,
            RootPath = root_path,
            Description = description
        });
        return JsonSerializer.Serialize(project, JsonOpts.Default);
    }

    [McpServerTool(Name = "list_projects"), Description("List all registered projects.")]
    public static async Task<string> ListProjects(IProjectRepository repo)
    {
        var projects = await repo.GetAllAsync();
        return JsonSerializer.Serialize(projects, JsonOpts.Default);
    }

    [McpServerTool(Name = "get_project"), Description("Get a project by ID with summary stats (task counts by status, unread messages).")]
    public static async Task<string> GetProject(
        IProjectRepository repo,
        [Description("Project ID.")] string project_id,
        [Description("Your agent identity, for unread message count.")] string? agent = null)
    {
        var stats = await repo.GetWithStatsAsync(project_id, agent);
        return JsonSerializer.Serialize(stats, JsonOpts.Default);
    }
}
