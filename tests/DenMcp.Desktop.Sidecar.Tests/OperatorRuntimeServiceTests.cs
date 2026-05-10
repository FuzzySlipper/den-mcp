using System.Net;
using System.Text;
using DenMcp.Desktop.Sidecar;

namespace DenMcp.Desktop.Sidecar.Tests;

public class OperatorRuntimeServiceTests
{
    [Fact]
    public async Task RefreshAsync_SuccessSyncsDenPublishesLocalSnapshotsAndEvents()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("repo");
        var service = CreateService(
            temp,
            new QueueHandler(
                JsonResponse("""
                    {"status":"healthy","version":"1.0"}
                    """),
                JsonResponse($$"""
                    [{"id":"den-mcp","name":"Den MCP","root_path":"{{Json(root)}}","description":null,"created_at":null,"updated_at":null}]
                    """),
                JsonResponse("[]"),
                JsonResponse("[]"),
                JsonResponse("{}")),
            new FakeGitRunner(StatusOutput()));

        await service.Runtime.StartAsync(runInitialRefresh: false, startBackgroundLoop: false);
        await service.Runtime.RefreshAsync();

        var status = await service.Runtime.GetStatusAsync();
        var snapshots = await service.Runtime.ListLocalSnapshotsAsync();

        Assert.Equal("connected", status.DenConnection.State);
        Assert.Equal(1, status.ProjectCount);
        Assert.Equal(1, status.LocalSnapshotCount);
        Assert.Equal("ready", status.ObserverStatuses.Single(observer => observer.Kind == "git").State);
        Assert.Equal("published", snapshots.Snapshots.Single().LastPublishStatus);
        Assert.Contains(DesktopSidecarProtocol.OperatorStatusEvent, service.Events.PublishedFrames.Select(frame => frame.Event));
        Assert.Contains(DesktopSidecarProtocol.GitSnapshotEvent, service.Events.PublishedFrames.Select(frame => frame.Event));
        Assert.Contains(DesktopSidecarProtocol.SessionSnapshotEvent, service.Events.PublishedFrames.Select(frame => frame.Event));
    }

    [Fact]
    public async Task PublishSnapshotsAsync_UsesRefreshCycleByDesignToPublishFreshSnapshotsAndEvents()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("repo");
        var service = CreateService(
            temp,
            new QueueHandler(
                JsonResponse("""
                    {"status":"healthy","version":"1.0"}
                    """),
                JsonResponse($$"""
                    [{"id":"den-mcp","name":"Den MCP","root_path":"{{Json(root)}}","description":null,"created_at":null,"updated_at":null}]
                    """),
                JsonResponse("[]"),
                JsonResponse("[]"),
                JsonResponse("{}")),
            new FakeGitRunner(StatusOutput()));

        await service.Runtime.StartAsync(runInitialRefresh: false, startBackgroundLoop: false);
        await service.Runtime.PublishSnapshotsAsync();

        var status = await service.Runtime.GetStatusAsync();
        var snapshots = await service.Runtime.ListLocalSnapshotsAsync();

        Assert.Equal("connected", status.DenConnection.State);
        Assert.Equal(1, status.ProjectCount);
        Assert.Equal("published", snapshots.Snapshots.Single().LastPublishStatus);
        Assert.Contains(service.Http.Requests, request =>
            request.Method == "PUT" && request.Uri.Contains("/desktop/git-snapshots", StringComparison.Ordinal));
        Assert.Contains(DesktopSidecarProtocol.GitSnapshotEvent, service.Events.PublishedFrames.Select(frame => frame.Event));
        Assert.Contains(DesktopSidecarProtocol.SessionSnapshotEvent, service.Events.PublishedFrames.Select(frame => frame.Event));
    }

    [Fact]
    public async Task RefreshAsync_OfflineKeepsCachedScopesAndQueuesLocalSnapshots()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("repo");
        var handler = new QueueHandler(
            JsonResponse("""
                {"status":"healthy"}
                """),
            JsonResponse($$"""
                [{"id":"den-mcp","name":"Den MCP","root_path":"{{Json(root)}}","description":null,"created_at":null,"updated_at":null}]
                """),
            JsonResponse("[]"),
            JsonResponse("[]"),
            JsonResponse("{}"),
            TransportFailure("Den is unavailable"));
        var service = CreateService(temp, handler, new FakeGitRunner(StatusOutput(), StatusOutput()));

        await service.Runtime.StartAsync(runInitialRefresh: false, startBackgroundLoop: false);
        await service.Runtime.RefreshAsync();
        await service.Runtime.RefreshAsync();

        var status = await service.Runtime.GetStatusAsync();
        var snapshots = await service.Runtime.ListLocalSnapshotsAsync();

        Assert.Equal("offline", status.DenConnection.State);
        Assert.Single(snapshots.Scopes);
        Assert.Equal("queued", snapshots.Snapshots.Single().LastPublishStatus);
        Assert.Contains("offline", snapshots.Snapshots.Single().LastPublishError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefreshAsync_DegradedWhenDenListsFailAfterHealth()
    {
        using var temp = TempDirectory.Create();
        var service = CreateService(
            temp,
            new QueueHandler(
                JsonResponse("""
                    {"status":"healthy"}
                    """),
                JsonResponse("{\"error\":\"broken\"}", HttpStatusCode.InternalServerError)),
            new FakeGitRunner());

        await service.Runtime.StartAsync(runInitialRefresh: false, startBackgroundLoop: false);
        await service.Runtime.RefreshAsync();

        var status = await service.Runtime.GetStatusAsync();

        Assert.Equal("degraded", status.DenConnection.State);
        Assert.Contains("HTTP 500", status.DenConnection.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshAsync_MisconfiguredUrlSkipsNetworkAndStopsObservers()
    {
        using var temp = TempDirectory.Create();
        var service = CreateService(temp, new QueueHandler(), new FakeGitRunner(), settings: OperatorSettings.CreateDefault(() => "desktop-test") with
        {
            DenBaseUrl = "not a url",
        });

        await service.Runtime.StartAsync(runInitialRefresh: false, startBackgroundLoop: false);
        await service.Runtime.RefreshAsync();

        var status = await service.Runtime.GetStatusAsync();

        Assert.Equal("misconfigured", status.DenConnection.State);
        Assert.All(status.ObserverStatuses, observer => Assert.Equal("stopped", observer.State));
        Assert.Empty(service.Http.Requests);
    }

    [Fact]
    public async Task Diagnostics_AreTruncatedToRingBufferLimit()
    {
        using var temp = TempDirectory.Create();
        var service = CreateService(temp, new QueueHandler(), new FakeGitRunner());
        await service.Runtime.StartAsync(runInitialRefresh: false, startBackgroundLoop: false);

        for (var i = 0; i < OperatorRuntimeService.MaxDiagnostics + 5; i++)
        {
            await service.Runtime.AddDiagnosticAsync("info", "test", $"entry-{i}");
        }

        var status = await service.Runtime.GetStatusAsync();

        Assert.Equal(OperatorRuntimeService.MaxDiagnostics, status.Diagnostics.Count);
        Assert.DoesNotContain(status.Diagnostics, entry => entry.Message == "entry-0");
        Assert.Equal("entry-204", status.Diagnostics[^1].Message);
    }

    [Fact]
    public async Task SaveSettings_PersistsNormalizedSettingsAndRefreshesStatus()
    {
        using var temp = TempDirectory.Create();
        var service = CreateService(
            temp,
            new QueueHandler(
                JsonResponse("""
                    {"status":"healthy"}
                    """),
                JsonResponse("[]"),
                JsonResponse("[]"),
                JsonResponse("[]")),
            new FakeGitRunner());
        await service.Runtime.StartAsync(runInitialRefresh: false, startBackgroundLoop: false);

        var saved = await service.Runtime.SaveSettingsAsync(new SaveOperatorSettingsRequest
        {
            DenBaseUrl = "http://den.test/",
            SourceDisplayName = "  Desktop Test  ",
            PollIntervalSeconds = 2,
            MaxChangedFiles = 5,
        });

        var status = await service.Runtime.GetStatusAsync();
        var reloaded = service.Settings.Load();

        Assert.Equal("http://den.test", saved.DenBaseUrl);
        Assert.Equal("Desktop Test", saved.SourceDisplayName);
        Assert.Equal(OperatorSettings.MinPollIntervalSeconds, saved.PollIntervalSeconds);
        Assert.Equal(OperatorSettings.MinChangedFiles, saved.MaxChangedFiles);
        Assert.Equal(saved, reloaded);
        Assert.Equal("connected", status.DenConnection.State);
    }

    [Fact]
    public async Task RefreshAsync_FetchesSpacesAndExposesAllSpaceMetadata()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("repo");
        var service = CreateService(
            temp,
            new QueueHandler(
                JsonResponse("""
                    {"status":"healthy","version":"1.0"}
                    """),
                JsonResponse($$"""
                    [{"id":"den-mcp","name":"Den MCP","root_path":"{{Json(root)}}","description":null,"created_at":null,"updated_at":null}]
                    """),
                JsonResponse("[]"),
                JsonResponse("""
                    [{"id":"personal-1","name":"Personal","kind":"personal","visibility":"normal"},{"id":"assistant-1","name":"Assistant","kind":"assistant","visibility":"normal"},{"id":"den-mcp","name":"Den MCP","kind":"project","visibility":"normal"}]
                    """),
                JsonResponse("{}")),
            new FakeGitRunner(StatusOutput()));

        await service.Runtime.StartAsync(runInitialRefresh: false, startBackgroundLoop: false);
        await service.Runtime.RefreshAsync();

        var status = await service.Runtime.GetStatusAsync();
        var spaces = await service.Runtime.ListSpacesAsync();

        Assert.Equal("connected", status.DenConnection.State);
        Assert.Equal(3, status.SpaceCount);
        Assert.Equal(3, status.Spaces.Count);
        Assert.Contains(status.Spaces, s => s.Id == "personal-1" && s.Kind == "personal");
        Assert.Contains(status.Spaces, s => s.Id == "assistant-1" && s.Kind == "assistant");
        Assert.Contains(status.Spaces, s => s.Id == "den-mcp" && s.Kind == "project");
        Assert.Equal(3, spaces.Count);
        Assert.Contains(service.Http.Requests, request =>
            request.Uri.Contains("/api/spaces", StringComparison.Ordinal)
            && request.Uri.Contains("includeHidden=true", StringComparison.Ordinal)
            && request.Uri.Contains("includeArchived=true", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RefreshAsync_AppliesConfiguredSpaceVisibilityPolicy()
    {
        using var temp = TempDirectory.Create();
        var service = CreateService(
            temp,
            new QueueHandler(
                JsonResponse("""
                    {"status":"healthy","version":"1.0"}
                    """),
                JsonResponse("[]"),
                JsonResponse("[]"),
                JsonResponse("[]")),
            new FakeGitRunner(),
            settings: OperatorSettings.CreateDefault(() => "desktop-test") with
            {
                IncludeHiddenSpaces = false,
                IncludeArchivedSpaces = false,
            });

        await service.Runtime.StartAsync(runInitialRefresh: false, startBackgroundLoop: false);
        await service.Runtime.RefreshAsync();

        Assert.Contains(service.Http.Requests, request =>
            request.Uri == "http://localhost:5199/api/spaces?includeHidden=false&includeArchived=false");
    }

    [Fact]
    public async Task GetLatestDiffSnapshot_UsesCurrentSettingsAndDefaultSourceInstanceId()
    {
        using var temp = TempDirectory.Create();
        var service = CreateService(
            temp,
            new QueueHandler(JsonResponse("""
                {"project_id":"den-mcp","task_id":1000,"workspace_id":null,"root_path":"/repo","path":"src/Foo.cs","source_instance_id":"desktop-test","state":"missing","is_stale":true,"freshness_status":"missing","snapshot":null}
                """)),
            new FakeGitRunner(),
            settings: OperatorSettings.CreateDefault(() => "desktop-test") with { DenBaseUrl = "http://den.test" });
        await service.Runtime.StartAsync(runInitialRefresh: false, startBackgroundLoop: false);

        var latest = await service.Runtime.GetLatestDiffSnapshotAsync(new LatestDiffSnapshotRequest
        {
            ProjectId = "den-mcp",
            TaskId = 1000,
            RootPath = "/repo",
            Path = "src/Foo.cs",
        });

        Assert.Equal(DesktopSnapshotState.Missing, latest.State);
        Assert.Contains("sourceInstanceId=desktop-test", service.Http.Requests.Single().Uri, StringComparison.Ordinal);
    }

    private static RuntimeHarness CreateService(
        TempDirectory temp,
        QueueHandler http,
        FakeGitRunner git,
        OperatorSettings? settings = null)
    {
        var settingsService = new OperatorSettingsService(
            OperatorSettingsStorage.ForPath(Path.Combine(temp.Path, OperatorSettingsStorage.SettingsFileName)),
            () => "desktop-test");
        if (settings is not null)
        {
            settingsService.Save(settings);
        }

        var options = DesktopSidecarFixtures.CreateFixtureOptions() with { ConfigPath = temp.Path };
        var sidecarState = new DesktopSidecarRuntimeState(options, new DateTimeOffset(2026, 4, 29, 12, 0, 0, TimeSpan.Zero));
        var events = new OperatorRuntimeBridgeEventSink(sidecarState);
        var registry = new OperatorSessionRegistry(() => new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc));
        var den = new DenHttpClient(new HttpClient(http));
        var tmux = new TmuxOperatorSessionService(
            new FakeTmuxCommandRunner(),
            registry,
            events,
            settingsService,
            den,
            () => new DateTimeOffset(2026, 4, 29, 12, 0, 0, TimeSpan.Zero));
        var direct = new DirectPtyOperatorSessionService(
            new FakeDirectPtyBackend(),
            registry,
            events,
            settingsService,
            den,
            () => new DateTimeOffset(2026, 4, 29, 12, 0, 0, TimeSpan.Zero));
        var terminals = new TerminalOperatorSessionService(registry, tmux, direct);
        var runtime = new OperatorRuntimeService(
            settingsService,
            den,
            new GitSnapshotBuilder(git),
            new PiSessionSnapshotBuilder(name => name == "PI_SUBAGENT_RUNS_DIR" ? Path.Combine(temp.Path, "runs") : null, () => "2026-04-29T12:00:00.000Z"),
            terminals,
            events,
            registry,
            () => new DateTimeOffset(2026, 4, 29, 12, 0, 0, TimeSpan.Zero));

        return new RuntimeHarness(runtime, events, http, settingsService);
    }

    private static string StatusOutput()
    {
        return "# branch.oid abc123\n# branch.head main\n# branch.upstream origin/main\n# branch.ab +0 -0\n";
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private static HttpRequestException TransportFailure(string message)
    {
        return new HttpRequestException(message);
    }

    private static string Json(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private sealed record RuntimeHarness(
        OperatorRuntimeService Runtime,
        OperatorRuntimeBridgeEventSink Events,
        QueueHandler Http,
        OperatorSettingsService Settings);

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "den-runtime-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public string CreateDirectory(string name)
        {
            var path = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class FakeGitRunner : IGitCommandRunner
    {
        private readonly Queue<string> _statusOutputs;

        public FakeGitRunner(params string[] statusOutputs)
        {
            _statusOutputs = new Queue<string>(statusOutputs);
        }

        public Task<GitCommandResult> RunGitAsync(string rootPath, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
        {
            if (args.Count > 0 && args[0] == "status")
            {
                return Task.FromResult(new GitCommandResult
                {
                    ExitCode = 0,
                    Stdout = _statusOutputs.Count == 0 ? StatusOutput() : _statusOutputs.Dequeue(),
                });
            }

            return Task.FromResult(new GitCommandResult { ExitCode = 0, Stdout = string.Empty });
        }
    }

    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Queue<object> _responses;

        public QueueHandler(params object[] responses)
        {
            _responses = new Queue<object>(responses);
        }

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(request.Method.Method, request.RequestUri?.AbsoluteUri ?? string.Empty, body));
            Assert.NotEmpty(_responses);
            var response = _responses.Dequeue();
            if (response is HttpRequestException exception)
            {
                throw exception;
            }

            return Assert.IsType<HttpResponseMessage>(response);
        }
    }

    private sealed record RecordedRequest(string Method, string Uri, string? Body);
}
