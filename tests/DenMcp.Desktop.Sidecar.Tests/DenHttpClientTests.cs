using System.Net;
using System.Text;
using System.Text.Json;
using DenMcp.Desktop.Sidecar;

namespace DenMcp.Desktop.Sidecar.Tests;

public class DenHttpClientTests
{
    [Fact]
    public async Task HealthAndProjects_ParseSuccessResponsesAndConstructExpectedUrls()
    {
        var handler = new RecordingHandler(
            JsonResponse("""
                {"status":"healthy","version":"1.0","informational_version":null,"commit":"abc"}
                """),
            JsonResponse("""
                [{"id":"den-mcp","name":"Den MCP","root_path":"/repo","description":null,"created_at":null,"updated_at":null}]
                """));
        var client = new DenHttpClient(new HttpClient(handler));

        var health = await client.HealthAsync("http://den.test/");
        var projects = await client.ListProjectsAsync("http://den.test");

        Assert.Equal("healthy", health.Status);
        Assert.Equal("abc", health.Commit);
        Assert.Single(projects);
        Assert.Equal("den-mcp", projects[0].Id);
        Assert.Equal("GET", handler.Requests[0].Method);
        Assert.Equal("http://den.test/health", handler.Requests[0].Uri);
        Assert.Equal("GET", handler.Requests[1].Method);
        Assert.Equal("http://den.test/api/projects", handler.Requests[1].Uri);
    }

    [Fact]
    public async Task ListSpaces_ParsesSnakeCaseResponseAndHitsExpectedUrl()
    {
        var handler = new RecordingHandler(JsonResponse("""
            [{"id":"personal-1","name":"Personal","kind":"personal","visibility":"normal","owner":"user-1","root_path":null,"description":null,"created_at":null,"updated_at":null},{"id":"assistant-1","name":"Assistant","kind":"assistant","visibility":"normal","owner":null,"root_path":null,"description":null,"created_at":null,"updated_at":null}]
            """));
        var client = new DenHttpClient(new HttpClient(handler));

        var spaces = await client.ListSpacesAsync("http://den.test", new DenSpaceListOptions
        {
            IncludeHidden = true,
            IncludeArchived = true,
        });

        Assert.Equal(2, spaces.Count);
        Assert.Equal("personal-1", spaces[0].Id);
        Assert.Equal("personal", spaces[0].Kind);
        Assert.Equal("assistant-1", spaces[1].Id);
        Assert.Equal("assistant", spaces[1].Kind);
        Assert.Equal("GET", handler.Requests[0].Method);
        Assert.Equal("http://den.test/api/spaces?includeHidden=true&includeArchived=true", handler.Requests[0].Uri);
    }

    [Fact]
    public async Task ListSpaces_UsesExplicitVisibilityPolicyInQuery()
    {
        var handler = new RecordingHandler(JsonResponse("[]"));
        var client = new DenHttpClient(new HttpClient(handler));

        await client.ListSpacesAsync("http://den.test", new DenSpaceListOptions
        {
            IncludeHidden = false,
            IncludeArchived = true,
        });

        Assert.Equal("http://den.test/api/spaces?includeHidden=false&includeArchived=true", handler.Requests[0].Uri);
    }

    [Fact]
    public async Task ListAgentWorkspaces_AddsLimitAndParsesSnakeCaseResponse()
    {
        var handler = new RecordingHandler(JsonResponse("""
            [{"id":"ws-1","project_id":"den-mcp","task_id":997,"branch":"task/997","worktree_path":"/repo-ws","base_branch":"main","base_commit":"base","head_commit":null,"state":"active","created_by_run_id":null,"dev_server_url":null,"preview_url":null,"cleanup_policy":"keep","changed_file_summary":{"count":2},"created_at":null,"updated_at":null}]
            """));
        var client = new DenHttpClient(new HttpClient(handler));

        var workspaces = await client.ListAgentWorkspacesAsync("http://den.test");

        Assert.Single(workspaces);
        Assert.Equal("ws-1", workspaces[0].Id);
        Assert.Equal("den-mcp", workspaces[0].ProjectId);
        Assert.Equal(997, workspaces[0].TaskId);
        Assert.Equal("/repo-ws", workspaces[0].WorktreePath);
        Assert.Equal("http://den.test/api/agent-workspaces?limit=200", handler.Requests[0].Uri);
        Assert.Equal(2, workspaces[0].ChangedFileSummary?.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task PublishSnapshots_UseEscapedProjectSegmentAndSnakeCasePayloads()
    {
        var handler = new RecordingHandler(
            JsonResponse("{}"),
            JsonResponse("{}"),
            JsonResponse("{}"));
        var client = new DenHttpClient(new HttpClient(handler));

        await client.PublishGitSnapshotAsync("http://den.test", "den mcp/team", GitSnapshot());
        await client.PublishDiffSnapshotAsync("http://den.test", "den mcp/team", DiffSnapshot());
        await client.PublishSessionSnapshotAsync("http://den.test", "den mcp/team", SessionSnapshot());

        Assert.Equal("PUT", handler.Requests[0].Method);
        Assert.Equal("http://den.test/api/projects/den%20mcp%2Fteam/desktop/git-snapshots", handler.Requests[0].Uri);
        using (var git = JsonDocument.Parse(handler.Requests[0].Body!))
        {
            Assert.Equal("path_not_visible", git.RootElement.GetProperty("state").GetString());
            Assert.False(git.RootElement.GetProperty("is_detached").GetBoolean());
            Assert.Equal("abc", git.RootElement.GetProperty("head_sha").GetString());
            Assert.Equal("desktop-test", git.RootElement.GetProperty("source_instance_id").GetString());
            Assert.False(git.RootElement.TryGetProperty("sourceInstanceId", out _));
        }

        Assert.Equal("http://den.test/api/projects/den%20mcp%2Fteam/desktop/diff-snapshots", handler.Requests[1].Uri);
        using (var diff = JsonDocument.Parse(handler.Requests[1].Body!))
        {
            Assert.Equal("main", diff.RootElement.GetProperty("base_ref").GetString());
            Assert.Equal(4096, diff.RootElement.GetProperty("max_bytes").GetInt32());
            Assert.Equal("desktop-test", diff.RootElement.GetProperty("source_instance_id").GetString());
        }

        Assert.Equal("http://den.test/api/projects/den%20mcp%2Fteam/desktop/session-snapshots", handler.Requests[2].Uri);
        using (var session = JsonDocument.Parse(handler.Requests[2].Body!))
        {
            Assert.Equal("session-1", session.RootElement.GetProperty("session_id").GetString());
            Assert.Equal("coding", session.RootElement.GetProperty("current_phase").GetString());
            Assert.True(session.RootElement.GetProperty("control_capabilities").GetProperty("can_stop").GetBoolean());
        }
    }

    [Fact]
    public async Task LatestDiffSnapshot_ConstructsCamelCaseQueryAndParsesSnakeCaseResult()
    {
        var handler = new RecordingHandler(JsonResponse("""
            {"project_id":"den-mcp","task_id":997,"workspace_id":"ws-1","root_path":"/repo","path":"src/Foo.cs","source_instance_id":"desktop-test","state":"ok","is_stale":false,"freshness_status":"fresh","snapshot":{"id":7,"project_id":"den-mcp","task_id":997,"workspace_id":"ws-1","root_path":"/repo","path":"src/Foo.cs","base_ref":"main","head_ref":"task/997","max_bytes":4096,"staged":false,"diff":"diff --git","truncated":false,"binary":false,"warnings":[],"source_instance_id":"desktop-test","source_display_name":"Desktop Test","observed_at":"2026-04-27T12:00:00.000Z","received_at":"2026-04-27T12:00:01.000Z","updated_at":"2026-04-27T12:00:01.000Z","is_stale":false,"freshness_seconds":1}}
            """));
        var client = new DenHttpClient(new HttpClient(handler));

        var latest = await client.LatestDiffSnapshotAsync("http://den.test/", new LatestDiffSnapshotRequest
        {
            ProjectId = "den mcp",
            TaskId = 997,
            WorkspaceId = "ws-1",
            RootPath = "/repo/root path",
            Path = " src/Foo.cs ",
            SourceInstanceId = "desktop-test",
        });

        Assert.Equal("den-mcp", latest.ProjectId);
        Assert.Equal(DesktopSnapshotState.Ok, latest.State);
        Assert.False(latest.IsStale);
        Assert.NotNull(latest.Snapshot);
        Assert.Equal("Desktop Test", latest.Snapshot.SourceDisplayName);
        Assert.Equal(
            "http://den.test/api/projects/den%20mcp/desktop/diff-snapshots/latest?sourceInstanceId=desktop-test&rootPath=%2Frepo%2Froot%20path&staleAfterSeconds=120&path=%20src%2FFoo.cs%20&workspaceId=ws-1&taskId=997",
            handler.Requests[0].Uri);
    }

    [Fact]
    public async Task LatestDiffSnapshot_OmitsBlankOptionalQueryParameters()
    {
        var handler = new RecordingHandler(JsonResponse("""
            {"project_id":"den-mcp","task_id":null,"workspace_id":null,"root_path":"/repo","path":null,"source_instance_id":"desktop-test","state":"missing","is_stale":true,"freshness_status":"missing","snapshot":null}
            """));
        var client = new DenHttpClient(new HttpClient(handler));

        var latest = await client.LatestDiffSnapshotAsync("http://den.test", new LatestDiffSnapshotRequest
        {
            ProjectId = "den-mcp",
            RootPath = "/repo",
            Path = "   ",
            WorkspaceId = "   ",
            SourceInstanceId = "desktop-test",
        });

        Assert.Equal(DesktopSnapshotState.Missing, latest.State);
        Assert.Equal("http://den.test/api/projects/den-mcp/desktop/diff-snapshots/latest?sourceInstanceId=desktop-test&rootPath=%2Frepo&staleAfterSeconds=120", handler.Requests[0].Uri);
    }

    [Fact]
    public async Task HttpErrorsIncludeStatusAndBodyWhereRustClientDoes()
    {
        var handler = new RecordingHandler(
            JsonResponse("{\"error\":\"bad snapshot\"}", HttpStatusCode.BadRequest),
            JsonResponse("{\"error\":\"missing\"}", HttpStatusCode.NotFound));
        var client = new DenHttpClient(new HttpClient(handler));

        var publishError = await Assert.ThrowsAsync<DenHttpClientException>(() =>
            client.PublishGitSnapshotAsync("http://den.test", "den-mcp", GitSnapshot()));
        var latestError = await Assert.ThrowsAsync<DenHttpClientException>(() =>
            client.LatestDiffSnapshotAsync("http://den.test", new LatestDiffSnapshotRequest
            {
                ProjectId = "den-mcp",
                RootPath = "/repo",
                SourceInstanceId = "desktop-test",
            }));

        Assert.Contains("HTTP 400", publishError.Message, StringComparison.Ordinal);
        Assert.Contains("bad snapshot", publishError.Message, StringComparison.Ordinal);
        Assert.Contains("HTTP 404", latestError.Message, StringComparison.Ordinal);
        Assert.Contains("missing", latestError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidUrlAndParseErrorsReportClientExceptions()
    {
        var invalidUrlClient = new DenHttpClient(new HttpClient(new RecordingHandler(JsonResponse("{}"))));
        var invalidUrlError = await Assert.ThrowsAsync<DenHttpClientException>(() =>
            invalidUrlClient.HealthAsync("not a url"));
        Assert.Contains("Invalid Den server URL", invalidUrlError.Message, StringComparison.Ordinal);

        var handler = new RecordingHandler(JsonResponse("{not json"));
        var parseClient = new DenHttpClient(new HttpClient(handler));
        var parseError = await Assert.ThrowsAsync<DenHttpClientException>(() =>
            parseClient.ListProjectsAsync("http://den.test"));
        Assert.Contains("Unable to parse Den projects", parseError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateHttpClient_UsesEightSecondDefaultTimeout()
    {
        using var httpClient = DenHttpClient.CreateHttpClient();

        Assert.Equal(TimeSpan.FromSeconds(8), httpClient.Timeout);
        Assert.Equal(DenHttpClient.DefaultTimeout, httpClient.Timeout);
    }

    private static DesktopGitSnapshotRequest GitSnapshot()
    {
        return new DesktopGitSnapshotRequest
        {
            TaskId = 997,
            RootPath = "/repo",
            State = DesktopSnapshotState.PathNotVisible,
            Branch = "main",
            IsDetached = false,
            HeadSha = "abc",
            DirtyCounts = new GitDirtyCounts { Total = 1, Modified = 1 },
            ChangedFiles = new[]
            {
                new GitFileStatus
                {
                    Path = "src/Foo.cs",
                    IndexStatus = "M",
                    WorktreeStatus = ".",
                    Category = "modified",
                },
            },
            SourceInstanceId = "desktop-test",
            ObservedAt = "2026-04-27T12:00:00.000Z",
        };
    }

    private static DesktopDiffSnapshotRequest DiffSnapshot()
    {
        return new DesktopDiffSnapshotRequest
        {
            TaskId = 997,
            WorkspaceId = "ws-1",
            RootPath = "/repo",
            Path = "src/Foo.cs",
            BaseRef = "main",
            HeadRef = "task/997",
            MaxBytes = 4096,
            Diff = "diff --git",
            SourceInstanceId = "desktop-test",
            ObservedAt = "2026-04-27T12:00:00.000Z",
        };
    }

    private static DesktopSessionSnapshotRequest SessionSnapshot()
    {
        return new DesktopSessionSnapshotRequest
        {
            TaskId = 997,
            WorkspaceId = "ws-1",
            SessionId = "session-1",
            AgentIdentity = "pi",
            Role = "coder",
            CurrentCommand = "dotnet test",
            CurrentPhase = "coding",
            RecentActivity = JsonElement("{\"items\":[]}"),
            ChildSessions = JsonElement("{\"items\":[]}"),
            ControlCapabilities = JsonElement("{\"can_stop\":true}"),
            SourceInstanceId = "desktop-test",
            ObservedAt = "2026-04-27T12:00:00.000Z",
        };
    }

    private static JsonElement JsonElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

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
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(
                request.Method.Method,
                request.RequestUri?.AbsoluteUri ?? string.Empty,
                body));

            Assert.NotEmpty(_responses);
            return _responses.Dequeue();
        }
    }

    private sealed record RecordedRequest(string Method, string Uri, string? Body);
}
