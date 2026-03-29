using System.ComponentModel;
using System.Text.Json;
using DenMcp.Core.Data;
using ModelContextProtocol.Server;

namespace DenMcp.Server.Tools;

[McpServerToolType]
public sealed class AgentTools
{
    [McpServerTool(Name = "list_active_agents"), Description(
        "List agents currently active on a project (or all projects). " +
        "Shows who else is working, useful for coordination.")]
    public static async Task<string> ListActiveAgents(
        IAgentSessionRepository repo,
        [Description("Project ID to filter by. Omit to see all active agents.")] string? project_id = null)
    {
        var sessions = await repo.ListActiveAsync(project_id);
        return JsonSerializer.Serialize(sessions, JsonOpts.Default);
    }
}
