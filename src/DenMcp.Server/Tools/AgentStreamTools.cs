using System.ComponentModel;
using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using ModelContextProtocol.Server;

namespace DenMcp.Server.Tools;

[McpServerToolType]
public sealed class AgentStreamTools
{
    [McpServerTool(Name = "list_agent_stream"), Description("List agent stream entries with optional global or project-scoped filters. Returns newest first.")]
    public static async Task<string> ListAgentStream(
        IAgentStreamRepository repo,
        [Description("Optional project filter. Omit for the global stream.")] string? project_id = null,
        [Description("Optional task filter.")] int? task_id = null,
        [Description("Optional dispatch filter.")] int? dispatch_id = null,
        [Description("Filter by stream kind: ops or message.")] string? stream_kind = null,
        [Description("Optional event type filter, e.g. review_requested or question.")] string? event_type = null,
        [Description("Optional sender filter.")] string? sender = null,
        [Description("Optional sender instance id filter.")] string? sender_instance_id = null,
        [Description("Optional recipient agent filter.")] string? recipient_agent = null,
        [Description("Optional recipient role filter.")] string? recipient_role = null,
        [Description("Optional recipient instance id filter.")] string? recipient_instance_id = null,
        [Description("Maximum entries to return. Default 50, max 200.")] int limit = 50)
    {
        AgentStreamKind? parsedKind = null;
        if (!string.IsNullOrWhiteSpace(stream_kind))
        {
            try
            {
                parsedKind = EnumExtensions.ParseAgentStreamKind(stream_kind);
            }
            catch (ArgumentException ex)
            {
                return JsonSerializer.Serialize(new { error = ex.Message }, JsonOpts.Default);
            }
        }

        var entries = await repo.ListAsync(new AgentStreamListOptions
        {
            ProjectId = project_id,
            TaskId = task_id,
            DispatchId = dispatch_id,
            StreamKind = parsedKind,
            EventType = event_type,
            Sender = sender,
            SenderInstanceId = sender_instance_id,
            RecipientAgent = recipient_agent,
            RecipientRole = recipient_role,
            RecipientInstanceId = recipient_instance_id,
            Limit = limit
        });

        return JsonSerializer.Serialize(entries, JsonOpts.Default);
    }

    [McpServerTool(Name = "get_agent_stream_entry"), Description("Get a single agent stream entry by id, optionally scoped to a project.")]
    public static async Task<string> GetAgentStreamEntry(
        IAgentStreamRepository repo,
        [Description("Agent stream entry ID.")] int entry_id,
        [Description("Optional project scope check.")] string? project_id = null)
    {
        var entry = await repo.GetByIdAsync(entry_id);
        if (entry is null || (project_id is not null && !string.Equals(entry.ProjectId, project_id, StringComparison.Ordinal)))
            return JsonSerializer.Serialize(new { error = $"Agent stream entry {entry_id} not found" }, JsonOpts.Default);

        return JsonSerializer.Serialize(entry, JsonOpts.Default);
    }
}
