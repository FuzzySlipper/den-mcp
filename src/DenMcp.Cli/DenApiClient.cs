using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DenMcp.Core.Models;
using Thread = DenMcp.Core.Models.Thread;

namespace DenMcp.Cli;

public sealed class DenApiClient : IDisposable
{
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public DenApiClient(string baseUrl)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
    }

    public void Dispose() => _http.Dispose();

    // Projects
    public async Task<Project> CreateProjectAsync(Project project) =>
        await PostAsync<Project>("api/projects", project);

    public async Task<List<Project>> ListProjectsAsync() =>
        await GetAsync<List<Project>>("api/projects");

    public async Task<ProjectWithStats> GetProjectAsync(string id, string? agent = null)
    {
        var query = agent is not null ? $"?agent={Uri.EscapeDataString(agent)}" : "";
        return await GetAsync<ProjectWithStats>($"api/projects/{Uri.EscapeDataString(id)}{query}");
    }

    // Tasks
    public async Task<ProjectTask> CreateTaskAsync(string projectId, ProjectTask task, int[]? dependsOn = null)
    {
        var body = new
        {
            task.Title,
            task.Description,
            Priority = task.Priority == 3 ? (int?)null : task.Priority,
            task.Tags,
            task.AssignedTo,
            DependsOn = dependsOn,
            task.ParentId
        };
        return await PostAsync<ProjectTask>($"api/projects/{Esc(projectId)}/tasks", body);
    }

    public async Task<List<TaskSummary>> ListTasksAsync(string projectId, string? status = null,
        string? assignedTo = null, string? tags = null, int? priority = null, int? parentId = null)
    {
        var query = BuildQuery(
            ("status", status), ("assignedTo", assignedTo), ("tags", tags),
            ("priority", priority?.ToString()), ("parentId", parentId?.ToString()));
        return await GetAsync<List<TaskSummary>>($"api/projects/{Esc(projectId)}/tasks{query}");
    }

    public async Task<TaskDetail> GetTaskAsync(string projectId, int taskId) =>
        await GetAsync<TaskDetail>($"api/projects/{Esc(projectId)}/tasks/{taskId}");

    public async Task<ReviewPacketResult> RequestReviewAsync(string projectId, int taskId, object body) =>
        await PostAsync<ReviewPacketResult>($"api/projects/{Esc(projectId)}/tasks/{taskId}/review/request", body);

    public async Task<ReviewPacketResult> PostReviewFindingsAsync(string projectId, int taskId, object body) =>
        await PostAsync<ReviewPacketResult>($"api/projects/{Esc(projectId)}/tasks/{taskId}/review/findings/post", body);

    public async Task<ProjectTask> UpdateTaskAsync(string projectId, int taskId, string agent,
        Dictionary<string, object?> changes)
    {
        changes["agent"] = agent;
        return await PutAsync<ProjectTask>($"api/projects/{Esc(projectId)}/tasks/{taskId}", changes);
    }

    public async Task<ProjectTask?> NextTaskAsync(string projectId, string? assignedTo = null)
    {
        var query = assignedTo is not null ? $"?assignedTo={Uri.EscapeDataString(assignedTo)}" : "";
        var json = await _http.GetStringAsync($"api/projects/{Esc(projectId)}/tasks/next{query}");
        var doc = JsonDocument.Parse(json);
        // If the response has "message" key, no task available
        if (doc.RootElement.TryGetProperty("message", out _))
            return null;
        return JsonSerializer.Deserialize<ProjectTask>(json, JsonOpts);
    }

    // Dispatch
    public async Task<List<DispatchEntry>> ListDispatchesAsync(
        string? projectId = null,
        string? targetAgent = null,
        string? status = null)
    {
        var query = BuildQuery(
            ("projectId", projectId),
            ("targetAgent", targetAgent),
            ("status", status));
        return await GetAsync<List<DispatchEntry>>($"api/dispatch{query}");
    }

    public async Task<DispatchEntry> GetDispatchAsync(int dispatchId) =>
        await GetAsync<DispatchEntry>($"api/dispatch/{dispatchId}");

    public async Task<DispatchEntry> ApproveDispatchAsync(int dispatchId, string decidedBy) =>
        await PostAsync<DispatchEntry>($"api/dispatch/{dispatchId}/approve", new { decided_by = decidedBy });

    public async Task<DispatchEntry> RejectDispatchAsync(int dispatchId, string decidedBy) =>
        await PostAsync<DispatchEntry>($"api/dispatch/{dispatchId}/reject", new { decided_by = decidedBy });

    // Messages
    public async Task<Message> SendMessageAsync(string projectId, string sender, string content,
        int? taskId = null, int? threadId = null, string? metadata = null, MessageIntent? intent = null)
    {
        var body = new { sender, content, task_id = taskId, thread_id = threadId, metadata, intent };
        return await PostAsync<Message>($"api/projects/{Esc(projectId)}/messages", body);
    }

    public async Task<List<Message>> GetMessagesAsync(string projectId, int? taskId = null,
        string? since = null, string? unreadFor = null, int? limit = null, MessageIntent? intent = null)
    {
        var query = BuildQuery(
            ("taskId", taskId?.ToString()), ("since", since),
            ("unreadFor", unreadFor), ("limit", limit?.ToString()),
            ("intent", intent?.ToDbValue()));
        return await GetAsync<List<Message>>($"api/projects/{Esc(projectId)}/messages{query}");
    }

    public async Task<Thread> GetThreadAsync(string projectId, int threadId) =>
        await GetAsync<Thread>($"api/projects/{Esc(projectId)}/messages/thread/{threadId}");

    public async Task<int> MarkReadAsync(string agent, int[] messageIds)
    {
        var body = new { agent, message_ids = messageIds };
        var response = await _http.PostAsJsonAsync("api/messages/mark-read", body, JsonOpts);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("marked").GetInt32();
    }

    // Documents
    public async Task<Document> StoreDocumentAsync(string projectId, Document doc)
    {
        var body = new { doc.Slug, doc.Title, doc.Content, doc_type = doc.DocType.ToString().ToLowerInvariant(), doc.Tags };
        return await PostAsync<Document>($"api/projects/{Esc(projectId)}/documents", body);
    }

    public async Task<Document?> GetDocumentAsync(string projectId, string slug)
    {
        var response = await _http.GetAsync($"api/projects/{Esc(projectId)}/documents/{Uri.EscapeDataString(slug)}");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Document>(JsonOpts);
    }

    public async Task<List<DocumentSummary>> ListDocumentsAsync(string? projectId = null,
        string? docType = null, string? tags = null)
    {
        if (projectId is not null)
        {
            var query = BuildQuery(("docType", docType), ("tags", tags));
            return await GetAsync<List<DocumentSummary>>($"api/projects/{Esc(projectId)}/documents{query}");
        }
        var globalQuery = BuildQuery(("projectId", projectId), ("docType", docType), ("tags", tags));
        return await GetAsync<List<DocumentSummary>>($"api/documents{globalQuery}");
    }

    public async Task<List<DocumentSearchResult>> SearchDocumentsAsync(string query, string? projectId = null)
    {
        if (projectId is not null)
            return await GetAsync<List<DocumentSearchResult>>(
                $"api/projects/{Esc(projectId)}/documents/search?query={Uri.EscapeDataString(query)}");
        return await GetAsync<List<DocumentSearchResult>>(
            $"api/documents/search?query={Uri.EscapeDataString(query)}");
    }

    // Blackboard
    public async Task<BlackboardEntry> StoreBlackboardEntryAsync(BlackboardEntry entry)
    {
        var body = new
        {
            entry.Slug,
            entry.Title,
            entry.Content,
            entry.Tags,
            idle_ttl_seconds = entry.IdleTtlSeconds
        };
        return await PostAsync<BlackboardEntry>("api/blackboard", body);
    }

    public async Task<BlackboardEntry?> GetBlackboardEntryAsync(string slug)
    {
        var response = await _http.GetAsync($"api/blackboard/{Uri.EscapeDataString(slug)}");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<BlackboardEntry>(JsonOpts);
    }

    public async Task<List<BlackboardEntrySummary>> ListBlackboardEntriesAsync(string? tags = null)
    {
        var query = BuildQuery(("tags", tags));
        return await GetAsync<List<BlackboardEntrySummary>>($"api/blackboard{query}");
    }

    public async Task DeleteBlackboardEntryAsync(string slug) =>
        await DeleteAsync<object>($"api/blackboard/{Uri.EscapeDataString(slug)}");

    public async Task<int> CleanupBlackboardEntriesAsync()
    {
        var result = await PostAsync<Dictionary<string, int>>("api/blackboard/cleanup", new { });
        return result.TryGetValue("deleted", out var deleted) ? deleted : 0;
    }

    // Agent guidance
    public async Task<ResolvedAgentGuidance> ResolveAgentGuidanceAsync(string projectId) =>
        await GetAsync<ResolvedAgentGuidance>($"api/projects/{Esc(projectId)}/agent-guidance");

    public async Task<List<AgentGuidanceEntry>> ListAgentGuidanceEntriesAsync(string projectId, bool includeGlobal = false)
    {
        var query = BuildQuery(("includeGlobal", includeGlobal ? "true" : null));
        return await GetAsync<List<AgentGuidanceEntry>>($"api/projects/{Esc(projectId)}/agent-guidance/entries{query}");
    }

    public async Task<AgentGuidanceEntry> StoreAgentGuidanceEntryAsync(string projectId, AgentGuidanceEntry entry)
    {
        var body = new
        {
            document_project_id = entry.DocumentProjectId,
            document_slug = entry.DocumentSlug,
            importance = entry.Importance.ToDbValue(),
            audience = entry.Audience,
            sort_order = entry.SortOrder,
            notes = entry.Notes
        };
        return await PostAsync<AgentGuidanceEntry>($"api/projects/{Esc(projectId)}/agent-guidance/entries", body);
    }

    public async Task DeleteAgentGuidanceEntryAsync(string projectId, int entryId) =>
        await DeleteAsync<object>($"api/projects/{Esc(projectId)}/agent-guidance/entries/{entryId}");

    // Librarian
    public async Task<LibrarianResponse> QueryLibrarianAsync(string projectId, string query,
        int? taskId = null, bool includeGlobal = true)
    {
        var body = new { query, task_id = taskId, include_global = includeGlobal };
        return await PostAsync<LibrarianResponse>($"api/projects/{Esc(projectId)}/librarian/query", body);
    }

    // Agents
    public async Task<List<AgentSession>> ListActiveAgentsAsync(string? projectId = null)
    {
        var query = projectId is not null ? $"?projectId={Uri.EscapeDataString(projectId)}" : "";
        return await GetAsync<List<AgentSession>>($"api/agents/active{query}");
    }

    public async Task CheckOutBySessionAsync(string sessionId)
    {
        var body = new { agent = "", project_id = "", session_id = sessionId };
        await PostAsync<object>($"api/agents/checkout", body);
    }

    // Helpers
    private async Task<T> GetAsync<T>(string url) =>
        await _http.GetFromJsonAsync<T>(url, JsonOpts)
        ?? throw new InvalidOperationException($"Null response from GET {url}");

    private async Task<T> PostAsync<T>(string url, object body)
    {
        var response = await _http.PostAsJsonAsync(url, body, JsonOpts);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOpts)
            ?? throw new InvalidOperationException($"Null response from POST {url}");
    }

    private async Task<T> PutAsync<T>(string url, object body)
    {
        var response = await _http.PutAsJsonAsync(url, body, JsonOpts);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOpts)
            ?? throw new InvalidOperationException($"Null response from PUT {url}");
    }

    private async Task<T> DeleteAsync<T>(string url)
    {
        var response = await _http.DeleteAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOpts)
            ?? throw new InvalidOperationException($"Null response from DELETE {url}");
    }

    private static string Esc(string s) => Uri.EscapeDataString(s);

    private static string BuildQuery(params (string key, string? value)[] parts)
    {
        var pairs = parts.Where(p => p.value is not null)
            .Select(p => $"{p.key}={Uri.EscapeDataString(p.value!)}");
        var result = string.Join("&", pairs);
        return result.Length > 0 ? $"?{result}" : "";
    }
}
