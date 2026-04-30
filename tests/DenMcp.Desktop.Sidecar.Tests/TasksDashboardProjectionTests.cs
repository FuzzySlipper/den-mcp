using System.Net;
using System.Text;
using DenMcp.Desktop.Sidecar;

namespace DenMcp.Desktop.Sidecar.Tests;

public class TasksDashboardProjectionTests
{
    [Fact]
    public async Task Projection_BuildsHierarchyPacketsRunsLifecycleAndSessionChips()
    {
        var handler = new RecordingHandler(
            JsonResponse("""
                [
                  {"id":1001,"project_id":"den-mcp","title":"Backend projection","status":"in_progress","priority":2,"assigned_to":"pi","parent_id":900,"tags":["desktop"],"dependency_count":0,"subtask_count":0,"created_at":"2026-04-30T00:00:00","updated_at":"2026-04-30T01:00:00"},
                  {"id":1002,"project_id":"den-mcp","title":"UI dashboard","status":"planned","priority":3,"assigned_to":"pi","parent_id":900,"tags":["desktop"],"dependency_count":1,"subtask_count":0,"created_at":"2026-04-30T00:00:00","updated_at":"2026-04-30T01:05:00"}
                ]
                """),
            Detail(900, "Roadmap", "in_progress", "[]", "[]"),
            Detail(900, "Roadmap", "in_progress", "[]", "[]"), Messages(), RunsEmpty(), StreamEmpty(),
            Detail(1001, "Backend projection", "in_progress", "[]", "[]"), Messages(), Runs(), Stream(),
            Detail(1002, "UI dashboard", "planned", "[{\"task_id\":1001,\"title\":\"Backend projection\",\"status\":\"in_progress\"}]", "[]"), MessagesEmpty(), RunsEmpty(), StreamEmpty());
        var sessions = new OperatorSessionRegistry(() => new DateTime(2026, 4, 30, 2, 0, 0, DateTimeKind.Utc));
        sessions.Register(new OperatorSession
        {
            SessionId = "session-1001",
            ProjectId = "den-mcp",
            TaskId = 1001,
            DisplayName = "Coder #1001",
            Kind = OperatorSessionKind.Agent,
            Backend = OperatorSessionBackend.Process,
            Status = OperatorSessionStatus.Running,
            Role = "coder",
            Capabilities = OperatorSessionCapabilities.FullControl("fixture"),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            SourceInstanceId = "desktop-fixture",
            RecentActivity = [new OperatorSessionActivityItem { Summary = "dotnet test passed" }],
        });
        var service = new TasksDashboardProjectionService(
            new DenHttpClient(new HttpClient(handler)),
            sessions,
            _ => Task.FromResult(OperatorSettings.CreateDefault(() => "desktop-fixture") with { DenBaseUrl = "http://den.test" }),
            () => new DateTimeOffset(2026, 4, 30, 3, 0, 0, TimeSpan.Zero));

        var snapshot = await service.GetSnapshotAsync(new TasksDashboardSnapshotRequest
        {
            ProjectId = "den-mcp",
            ParentTaskId = 900,
        }, CancellationToken.None);

        Assert.Equal("den-mcp", snapshot.ProjectId);
        Assert.Equal(3, snapshot.Tasks.Count);
        Assert.Contains(snapshot.Tasks, task => task.Id == 1001 && task.Stage == "validation_complete");
        Assert.Contains(snapshot.Tasks.Single(task => task.Id == 1001).Packets, packet => packet.PacketType == "implementation_packet");
        Assert.Equal(0, snapshot.Tasks.Single(task => task.Id == 1001).WaveIndex);
        Assert.Equal(1, snapshot.Tasks.Single(task => task.Id == 1002).WaveIndex);
        Assert.Equal("blocked", snapshot.Tasks.Single(task => task.Id == 1002).ComputedState);
        Assert.Equal(1234, snapshot.Tasks.Single(task => task.Id == 1001).RunSummary.TotalTokens);
        Assert.Equal(0.42, snapshot.Header.TotalCost);
        Assert.Contains(snapshot.Lanes, lane => lane.LaneKey == "run:run-1" && lane.SessionChips.Count == 1);
        Assert.Contains(snapshot.Tasks.Single(task => task.Id == 1001).SessionChips, chip => chip.Capabilities.CanOpenExternalAttach);
        Assert.False(snapshot.Freshness.IsPartial);
        Assert.Contains(snapshot.Freshness.Warnings, warning => warning.Contains("Merge eligibility", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, request => request.Uri == "http://den.test/api/projects/den-mcp/tasks?parentId=900");
    }

    private static HttpResponseMessage Detail(long id, string title, string status, string dependencies, string reviewRounds)
    {
        return JsonResponse($$"""
            {"task":{"id":{{id}},"project_id":"den-mcp","title":"{{title}}","status":"{{status}}","priority":2,"assigned_to":"pi","parent_id":{{(id == 900 ? "null" : "900")}},"tags":["desktop"],"dependency_count":0,"subtask_count":0,"created_at":"2026-04-30T00:00:00","updated_at":"2026-04-30T01:00:00"},"dependencies":{{dependencies}},"subtasks":[],"recent_messages":[],"review_rounds":{{reviewRounds}},"open_review_findings":[],"resolved_review_findings":[]}
            """);
    }

    private static HttpResponseMessage Messages()
    {
        return JsonResponse("""
            [
              {"id":1,"sender":"pi","content":"# Implementation\nFiles changed: projection","intent":"handoff","metadata":{"type":"implementation_packet"},"created_at":"2026-04-30T01:10:00"},
              {"id":2,"sender":"pi","content":"Validation pass","intent":"handoff","metadata":{"type":"validation_packet"},"created_at":"2026-04-30T01:20:00"}
            ]
            """);
    }

    private static HttpResponseMessage MessagesEmpty() => JsonResponse("[]");

    private static HttpResponseMessage Runs()
    {
        return JsonResponse("""
            [{"run_id":"run-1","state":"completed","role":"coder","task_id":1001,"project_id":"den-mcp","model":"gpt-test","purpose":"implementation","worktree_path":"/repo","branch":"task/1001","head_commit":"abc","started_at":"2026-04-30T01:00:00Z","ended_at":"2026-04-30T01:30:00Z","duration_ms":1800000,"usage_summary":{"input_tokens":1000,"output_tokens":234,"total_tokens":1234,"total_cost":0.42,"currency":"USD","source":"fixture"},"operator_events":[{"event_name":"coder_completed","source":"agent_stream","occurred_at":"2026-04-30T01:30:00Z","visibility":"summary"}]}]
            """);
    }

    private static HttpResponseMessage RunsEmpty() => JsonResponse("[]");

    private static HttpResponseMessage Stream()
    {
        return JsonResponse("""
            [{"id":7,"stream_kind":"ops","event_type":"subagent_completed","project_id":"den-mcp","task_id":1001,"sender":"pi","recipient_agent":null,"body":"Coder completed run","metadata":{"run_id":"run-1"},"created_at":"2026-04-30T01:31:00Z"}]
            """);
    }

    private static HttpResponseMessage StreamEmpty() => JsonResponse("[]");

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public RecordingHandler(params HttpResponseMessage[] responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(request.Method.Method, request.RequestUri?.AbsoluteUri ?? string.Empty, body));
            Assert.NotEmpty(_responses);
            return _responses.Dequeue();
        }
    }

    private sealed record RecordedRequest(string Method, string Uri, string? Body);
}
