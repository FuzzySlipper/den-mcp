using System.Net;
using System.Text;
using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using DenMcp.Server.Notifications;
using Microsoft.Extensions.Logging.Abstractions;

namespace DenMcp.Server.Tests;

public class SignalNotificationChannelTests : IAsyncLifetime
{
    private string _dbPath = null!;
    private DbConnectionFactory _db = null!;
    private DispatchRepository _dispatches = null!;
    private NotificationMessageRepository _links = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"den-mcp-signal-test-{Guid.NewGuid()}.db");
        var initializer = new DatabaseInitializer(_dbPath, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();
        _db = new DbConnectionFactory(initializer.ConnectionString);
        _dispatches = new DispatchRepository(_db);
        _links = new NotificationMessageRepository(_db);

        var projects = new ProjectRepository(_db);
        await projects.CreateAsync(new Project { Id = "proj", Name = "Test Project" });
    }

    public Task DisposeAsync()
    {
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
        return Task.CompletedTask;
    }

    private async Task<DispatchEntry> CreateDispatchAsync(int triggerId = 1)
    {
        var (dispatch, _) = await _dispatches.CreateIfAbsentAsync(new DispatchEntry
        {
            ProjectId = "proj",
            TargetAgent = "claude-code",
            TriggerType = DispatchTriggerType.Message,
            TriggerId = triggerId,
            Summary = $"Dispatch {triggerId}",
            ContextPrompt = $"Prompt {triggerId}",
            DedupKey = DispatchEntry.BuildDedupKey(DispatchTriggerType.Message, triggerId, "claude-code"),
            ExpiresAt = DateTime.UtcNow.AddHours(4)
        });
        return dispatch;
    }

    [Fact]
    public async Task SendDispatchNotificationAsync_SendsSignalMessageAndStoresTimestampMapping()
    {
        var dispatch = await CreateDispatchAsync();
        var handler = new StubSignalHandler(snapshot =>
        {
            if (snapshot.Method == HttpMethod.Get && snapshot.Path == "/api/v1/check")
                return new HttpResponseMessage(HttpStatusCode.OK);

            if (snapshot.Method == HttpMethod.Post && snapshot.Path == "/api/v1/rpc")
            {
                return JsonResponse(new
                {
                    jsonrpc = "2.0",
                    result = new { timestamp = 1712345678901L },
                    id = "1"
                });
            }

            throw new InvalidOperationException($"Unexpected request: {snapshot.Method} {snapshot.Path}");
        });

        await using var channel = CreateChannel(handler);
        await channel.SendDispatchNotificationAsync(dispatch, "codex posted review feedback on #42");

        Assert.Equal(dispatch.Id, await _links.FindDispatchIdAsync("signal", "1712345678901"));

        var rpcRequest = Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
        using var requestJson = JsonDocument.Parse(rpcRequest.Body!);
        Assert.Equal("send", requestJson.RootElement.GetProperty("method").GetString());
        var parameters = requestJson.RootElement.GetProperty("params");
        Assert.Equal("+15551234567", parameters.GetProperty("recipient")[0].GetString());

        var message = parameters.GetProperty("message").GetString();
        Assert.Contains("codex posted review feedback on #42", message);
        Assert.Contains("React ✅ or 👍 to approve, ❌ or 👎 to reject.", message);
    }

    [Fact]
    public async Task SendDispatchNotificationAsync_WithUsernameRecipient_UsesUsernamesPayload()
    {
        var dispatch = await CreateDispatchAsync();
        var handler = new StubSignalHandler(snapshot =>
        {
            if (snapshot.Method == HttpMethod.Get && snapshot.Path == "/api/v1/check")
                return new HttpResponseMessage(HttpStatusCode.OK);

            if (snapshot.Method == HttpMethod.Post && snapshot.Path == "/api/v1/rpc")
            {
                return JsonResponse(new
                {
                    jsonrpc = "2.0",
                    result = new
                    {
                        timestamp = 1712345678902L,
                        results = new[]
                        {
                            new
                            {
                                recipientAddress = new
                                {
                                    username = "patchfoot.02",
                                    uuid = "12150588-774a-4698-8bca-075297d373c3"
                                },
                                type = "SUCCESS"
                            }
                        }
                    },
                    id = "username-send"
                });
            }

            throw new InvalidOperationException($"Unexpected request: {snapshot.Method} {snapshot.Path}");
        });

        await using var channel = CreateChannel(handler, signal =>
        {
            signal.Recipient = "patchfoot.02";
            signal.RecipientNumber = null;
        });

        await channel.SendDispatchNotificationAsync(dispatch, "username-targeted dispatch");

        var rpcRequest = Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
        using var requestJson = JsonDocument.Parse(rpcRequest.Body!);
        var parameters = requestJson.RootElement.GetProperty("params");
        Assert.Equal("patchfoot.02", parameters.GetProperty("usernames")[0].GetString());
        Assert.False(parameters.TryGetProperty("recipient", out _));
    }

    [Fact]
    public async Task StartListeningAsync_ApprovesDispatchFromSignalReaction()
    {
        var dispatch = await CreateDispatchAsync();
        await _links.LinkDispatchMessageAsync("signal", "1712345678901", dispatch.Id, "+15551234567");

        var payload = """
            event:receive
            data:{"account":"+15550001111","envelope":{"sourceNumber":"+15551234567","timestamp":1712350000000,"dataMessage":{"timestamp":1712350000000,"reaction":{"emoji":"✅","targetSentTimestamp":1712345678901,"isRemove":false}}}}

            """;

        var handler = new StubSignalHandler(snapshot =>
        {
            if (snapshot.Method == HttpMethod.Get && snapshot.Path == "/api/v1/check")
                return new HttpResponseMessage(HttpStatusCode.OK);

            if (snapshot.Method == HttpMethod.Get && snapshot.Path == "/api/v1/events")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "text/event-stream")
                };
            }

            if (snapshot.Method == HttpMethod.Post && snapshot.Path == "/api/v1/rpc")
            {
                return JsonResponse(new
                {
                    jsonrpc = "2.0",
                    result = new { timestamp = 1712350000001L },
                    id = "2"
                });
            }

            throw new InvalidOperationException($"Unexpected request: {snapshot.Method} {snapshot.Path}");
        });

        await using var channel = CreateChannel(handler);
        await channel.StartListeningAsync(CancellationToken.None);

        Assert.Contains("signal-events", handler.RequestedClientNames);

        var approved = await _dispatches.GetByIdAsync(dispatch.Id);
        Assert.NotNull(approved);
        Assert.Equal(DispatchStatus.Approved, approved.Status);
        Assert.Equal("signal:+15551234567", approved.DecidedBy);
    }

    [Fact]
    public async Task StartListeningAsync_ApprovesDispatchFromThumbsUpReaction()
    {
        var dispatch = await CreateDispatchAsync(triggerId: 11);
        await _links.LinkDispatchMessageAsync("signal", "1712345678911", dispatch.Id, "+15551234567");

        var payload = """
            event:receive
            data:{"account":"+15550001111","envelope":{"sourceNumber":"+15551234567","timestamp":1712350000011,"dataMessage":{"timestamp":1712350000011,"reaction":{"emoji":"👍","targetSentTimestamp":1712345678911,"isRemove":false}}}}

            """;

        var handler = new StubSignalHandler(snapshot =>
        {
            if (snapshot.Method == HttpMethod.Get && snapshot.Path == "/api/v1/check")
                return new HttpResponseMessage(HttpStatusCode.OK);

            if (snapshot.Method == HttpMethod.Get && snapshot.Path == "/api/v1/events")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "text/event-stream")
                };
            }

            if (snapshot.Method == HttpMethod.Post && snapshot.Path == "/api/v1/rpc")
            {
                return JsonResponse(new
                {
                    jsonrpc = "2.0",
                    result = new { timestamp = 1712350000012L },
                    id = "thumbs-up"
                });
            }

            throw new InvalidOperationException($"Unexpected request: {snapshot.Method} {snapshot.Path}");
        });

        await using var channel = CreateChannel(handler);
        await channel.StartListeningAsync(CancellationToken.None);

        var approved = await _dispatches.GetByIdAsync(dispatch.Id);
        Assert.NotNull(approved);
        Assert.Equal(DispatchStatus.Approved, approved.Status);
        Assert.Equal("signal:+15551234567", approved.DecidedBy);
    }

    [Fact]
    public async Task StartListeningAsync_WithUsernameRecipient_MatchesReactionUsingRememberedRecipientIdentity()
    {
        var dispatch = await CreateDispatchAsync();
        var handler = new StubSignalHandler(snapshot =>
        {
            if (snapshot.Method == HttpMethod.Get && snapshot.Path == "/api/v1/check")
                return new HttpResponseMessage(HttpStatusCode.OK);

            if (snapshot.Method == HttpMethod.Post && snapshot.Path == "/api/v1/rpc")
            {
                return JsonResponse(new
                {
                    jsonrpc = "2.0",
                    result = new
                    {
                        timestamp = snapshot.Body!.Contains("username-targeted dispatch", StringComparison.Ordinal)
                            ? 1712345678903L
                            : 1712350000002L,
                        results = new[]
                        {
                            new
                            {
                                recipientAddress = new
                                {
                                    username = "patchfoot.02",
                                    uuid = "12150588-774a-4698-8bca-075297d373c3"
                                },
                                type = "SUCCESS"
                            }
                        }
                    },
                    id = "username-rpc"
                });
            }

            if (snapshot.Method == HttpMethod.Get && snapshot.Path == "/api/v1/events")
            {
                var payload = """
                    event:receive
                    data:{"account":"+15550001111","envelope":{"sourceUuid":"12150588-774a-4698-8bca-075297d373c3","timestamp":1712350000000,"dataMessage":{"timestamp":1712350000000,"reaction":{"emoji":"✅","targetSentTimestamp":1712345678903,"isRemove":false}}}}

                    """;

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "text/event-stream")
                };
            }

            throw new InvalidOperationException($"Unexpected request: {snapshot.Method} {snapshot.Path}");
        });

        await using var channel = CreateChannel(handler, signal =>
        {
            signal.Recipient = "patchfoot.02";
            signal.RecipientNumber = null;
        });

        await channel.SendDispatchNotificationAsync(dispatch, "username-targeted dispatch");
        await channel.StartListeningAsync(CancellationToken.None);

        var approved = await _dispatches.GetByIdAsync(dispatch.Id);
        Assert.NotNull(approved);
        Assert.Equal(DispatchStatus.Approved, approved.Status);
        Assert.Equal("signal:12150588-774a-4698-8bca-075297d373c3", approved.DecidedBy);
    }

    [Fact]
    public async Task StartListeningAsync_RejectsDispatchFromThumbsDownReaction()
    {
        var dispatch = await CreateDispatchAsync(triggerId: 12);
        await _links.LinkDispatchMessageAsync("signal", "1712345678912", dispatch.Id, "+15551234567");

        var payload = """
            event:receive
            data:{"account":"+15550001111","envelope":{"sourceNumber":"+15551234567","timestamp":1712350000012,"dataMessage":{"timestamp":1712350000012,"reaction":{"emoji":"👎","targetSentTimestamp":1712345678912,"isRemove":false}}}}

            """;

        var handler = new StubSignalHandler(snapshot =>
        {
            if (snapshot.Method == HttpMethod.Get && snapshot.Path == "/api/v1/check")
                return new HttpResponseMessage(HttpStatusCode.OK);

            if (snapshot.Method == HttpMethod.Get && snapshot.Path == "/api/v1/events")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "text/event-stream")
                };
            }

            if (snapshot.Method == HttpMethod.Post && snapshot.Path == "/api/v1/rpc")
            {
                return JsonResponse(new
                {
                    jsonrpc = "2.0",
                    result = new { timestamp = 1712350000013L },
                    id = "thumbs-down"
                });
            }

            throw new InvalidOperationException($"Unexpected request: {snapshot.Method} {snapshot.Path}");
        });

        await using var channel = CreateChannel(handler);
        await channel.StartListeningAsync(CancellationToken.None);

        var rejected = await _dispatches.GetByIdAsync(dispatch.Id);
        Assert.NotNull(rejected);
        Assert.Equal(DispatchStatus.Rejected, rejected.Status);
        Assert.Equal("signal:+15551234567", rejected.DecidedBy);
    }

    [Fact]
    public async Task SendDispatchNotificationAsync_WhenDaemonUnavailable_DoesNotThrow()
    {
        var dispatch = await CreateDispatchAsync();
        var handler = new StubSignalHandler(_ => throw new HttpRequestException("Connection refused"));

        await using var channel = CreateChannel(handler);
        await channel.SendDispatchNotificationAsync(dispatch, "No daemon available");

        Assert.Null(await _links.FindDispatchIdAsync("signal", "1712345678901"));
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    private SignalNotificationChannel CreateChannel(
        StubSignalHandler handler,
        Action<SignalOptions>? configure = null)
    {
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:8080")
        };

        var options = new DenMcpOptions
        {
            Signal = new SignalOptions
            {
                Enabled = true,
                Recipient = null,
                RecipientNumber = "+15551234567",
                Account = "+15550001111",
                AutoStart = false,
                NotifyOnDispatch = true,
                NotifyOnAgentStatus = true
            }
        };

        configure?.Invoke(options.Signal);

        return new SignalNotificationChannel(
            options,
            new StubHttpClientFactory(client, handler.RequestedClientNames),
            _dispatches,
            _links,
            NullLogger<SignalNotificationChannel>.Instance);
    }

    private static HttpResponseMessage JsonResponse(object body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        private readonly List<string> _requestedClientNames;

        public StubHttpClientFactory(HttpClient client, List<string> requestedClientNames)
        {
            _client = client;
            _requestedClientNames = requestedClientNames;
        }

        public HttpClient CreateClient(string name)
        {
            _requestedClientNames.Add(name);
            return _client;
        }
    }

    private sealed class StubSignalHandler(Func<RequestSnapshot, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private readonly Func<RequestSnapshot, HttpResponseMessage> _responder = responder;

        public List<RequestSnapshot> Requests { get; } = [];
        public List<string> RequestedClientNames { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var snapshot = new RequestSnapshot(
                request.Method,
                request.RequestUri?.AbsolutePath ?? string.Empty,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));

            Requests.Add(snapshot);
            return _responder(snapshot);
        }
    }

    private sealed record RequestSnapshot(HttpMethod Method, string Path, string? Body);
}
