using System.Net;
using System.Text;
using DenMcp.Desktop.Sidecar;

namespace DenMcp.Desktop.Sidecar.Tests;

public class MessagesProjectionTests
{
    [Fact]
    public async Task Projection_ReturnsMessagesWithMetadataTypeAndSummary()
    {
        var handler = new RecordingHandler(
            JsonResponse("""
                [
                  {"id":1,"project_id":"den-mcp","task_id":1092,"thread_id":null,"sender":"pi","content":"# Coder Context Packet\nTask: #1092","intent":"handoff","metadata":{"type":"coder_context_packet"},"created_at":"2026-04-30T01:00:00"},
                  {"id":2,"project_id":"den-mcp","task_id":1092,"thread_id":1,"sender":"pi","content":"Implementation done\nFiles changed: 5","intent":"handoff","metadata":{"type":"implementation_packet"},"created_at":"2026-04-30T02:00:00"},
                  {"id":3,"project_id":"den-mcp","task_id":null,"thread_id":null,"sender":"user","content":"Looking good","intent":null,"metadata":null,"created_at":"2026-04-30T03:00:00"}
                ]
                """));

        var service = CreateService(handler);

        var snapshot = await service.GetSnapshotAsync(new MessagesSnapshotRequest
        {
            ProjectId = "den-mcp",
        }, CancellationToken.None);

        Assert.Equal("den-mcp", snapshot.ProjectId);
        Assert.Equal(3, snapshot.Messages.Count);
        Assert.False(snapshot.Freshness.IsPartial);

        // First message: coder context packet with task_id
        var first = snapshot.Messages[0];
        Assert.Equal(1, first.Id);
        Assert.Equal("pi", first.Sender);
        Assert.Equal(1092, first.TaskId);
        Assert.Null(first.ThreadId);
        Assert.Equal("coder_context_packet", first.MetadataType);
        Assert.Equal("# Coder Context Packet Task: #1092", first.ContentSummary);
        Assert.Equal("handoff", first.Intent);

        // Second message: implementation packet with thread_id (reply)
        var second = snapshot.Messages[1];
        Assert.Equal("implementation_packet", second.MetadataType);
        Assert.Equal(2, second.Id);
        Assert.Equal(1092, second.TaskId);
        Assert.Equal(1, second.ThreadId);

        // Third message: no metadata type, no task/thread
        var third = snapshot.Messages[2];
        Assert.Null(third.MetadataType);
        Assert.Null(third.TaskId);
        Assert.Null(third.ThreadId);
        Assert.Equal("user", third.Sender);
        Assert.Equal("Looking good", third.ContentSummary);

        // Verify request URL
        Assert.Contains(handler.Requests, r => r.Uri == "http://den.test/api/projects/den-mcp/messages?limit=20");
    }

    [Fact]
    public async Task Projection_TaskFilter_PassesTaskId()
    {
        var handler = new RecordingHandler(
            JsonResponse("""
                [
                  {"id":10,"project_id":"den-mcp","task_id":1092,"thread_id":null,"sender":"pi","content":"Task message","intent":"handoff","metadata":null,"created_at":"2026-04-30T01:00:00"}
                ]
                """));

        var service = CreateService(handler);

        var snapshot = await service.GetSnapshotAsync(new MessagesSnapshotRequest
        {
            ProjectId = "den-mcp",
            TaskId = 1092,
        }, CancellationToken.None);

        Assert.Single(snapshot.Messages);
        Assert.Equal(1092, snapshot.Messages[0].TaskId);
        Assert.Contains(handler.Requests, r => r.Uri.Contains("taskId=1092", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Projection_LimitClamped()
    {
        var handler = new RecordingHandler(
            JsonResponse("[]"));

        var service = CreateService(handler);

        // Request with limit 0 — should be clamped to 1
        await service.GetSnapshotAsync(new MessagesSnapshotRequest
        {
            ProjectId = "den-mcp",
            Limit = 0,
        }, CancellationToken.None);

        Assert.Contains(handler.Requests, r => r.Uri.Contains("limit=1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Projection_DenError_PartialFreshness()
    {
        var handler = new RecordingHandler(
            JsonResponseWithError(HttpStatusCode.InternalServerError));

        var service = CreateService(handler);

        var snapshot = await service.GetSnapshotAsync(new MessagesSnapshotRequest
        {
            ProjectId = "den-mcp",
        }, CancellationToken.None);

        Assert.Empty(snapshot.Messages);
        Assert.True(snapshot.Freshness.IsPartial);
        Assert.NotEmpty(snapshot.Freshness.Errors);
    }

    [Fact]
    public async Task Projection_TruncatesLongContent()
    {
        var longContent = new string('x', 500);
        var handler = new RecordingHandler(
            JsonResponse($"[{{\"id\":1,\"project_id\":\"den-mcp\",\"task_id\":null,\"thread_id\":null,\"sender\":\"pi\",\"content\":\"{longContent}\",\"intent\":null,\"metadata\":null,\"created_at\":\"2026-04-30T01:00:00\"}}]"));

        var service = CreateService(handler);

        var snapshot = await service.GetSnapshotAsync(new MessagesSnapshotRequest
        {
            ProjectId = "den-mcp",
        }, CancellationToken.None);

        Assert.Single(snapshot.Messages);
        Assert.Equal(281, snapshot.Messages[0].ContentSummary.Length); // 280 chars + ellipsis
        Assert.EndsWith("…", snapshot.Messages[0].ContentSummary);
    }

    [Fact]
    public async Task Projection_EmptyMessages_ReturnsEmptySnapshot()
    {
        var handler = new RecordingHandler(
            JsonResponse("[]"));

        var service = CreateService(handler);

        var snapshot = await service.GetSnapshotAsync(new MessagesSnapshotRequest
        {
            ProjectId = "den-mcp",
        }, CancellationToken.None);

        Assert.Empty(snapshot.Messages);
        Assert.Equal(0, snapshot.TotalCount);
        Assert.Equal(0, snapshot.UnreadCount);
        Assert.Null(snapshot.ThreadRoot);
        Assert.False(snapshot.Freshness.IsPartial);
    }

    [Fact]
    public async Task Projection_UnreadFor_MarksUnreadMessages()
    {
        // Two responses: all messages, then unread-filtered messages
        var handler = new RecordingHandler(
            JsonResponse("""
                [
                  {"id":1,"project_id":"den-mcp","task_id":1092,"thread_id":null,"sender":"pi","content":"Read message","intent":"handoff","metadata":null,"created_at":"2026-04-30T01:00:00"},
                  {"id":2,"project_id":"den-mcp","task_id":1092,"thread_id":null,"sender":"pi","content":"Unread message","intent":"handoff","metadata":null,"created_at":"2026-04-30T02:00:00"},
                  {"id":3,"project_id":"den-mcp","task_id":null,"thread_id":null,"sender":"user","content":"Another unread","intent":null,"metadata":null,"created_at":"2026-04-30T03:00:00"}
                ]
                """),
            JsonResponse("""
                [
                  {"id":2,"project_id":"den-mcp","task_id":1092,"thread_id":null,"sender":"pi","content":"Unread message","intent":"handoff","metadata":null,"created_at":"2026-04-30T02:00:00"},
                  {"id":3,"project_id":"den-mcp","task_id":null,"thread_id":null,"sender":"user","content":"Another unread","intent":null,"metadata":null,"created_at":"2026-04-30T03:00:00"}
                ]
                """));

        var service = CreateService(handler);

        var snapshot = await service.GetSnapshotAsync(new MessagesSnapshotRequest
        {
            ProjectId = "den-mcp",
            UnreadFor = "pi",
        }, CancellationToken.None);

        Assert.Equal(3, snapshot.Messages.Count);
        Assert.Equal(2, snapshot.UnreadCount);

        Assert.False(snapshot.Messages[0].IsUnread); // id=1 was read
        Assert.True(snapshot.Messages[1].IsUnread);  // id=2 is unread
        Assert.True(snapshot.Messages[2].IsUnread);  // id=3 is unread

        // Second request should include unreadFor=pi
        Assert.Contains(handler.Requests, r => r.Uri.Contains("unreadFor=pi", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Projection_ThreadRoot_FetchesSingleMessage()
    {
        // Two responses: message list, then single thread root
        var handler = new RecordingHandler(
            JsonResponse("""
                [
                  {"id":2,"project_id":"den-mcp","task_id":1092,"thread_id":1,"sender":"pi","content":"Reply","intent":"handoff","metadata":null,"created_at":"2026-04-30T02:00:00"}
                ]
                """),
            JsonResponse("""
                  {"id":1,"project_id":"den-mcp","task_id":1092,"thread_id":null,"sender":"pi","content":"Thread root","intent":"handoff","metadata":{"type":"coder_context_packet"},"created_at":"2026-04-30T01:00:00"}
                """));

        var service = CreateService(handler);

        var snapshot = await service.GetSnapshotAsync(new MessagesSnapshotRequest
        {
            ProjectId = "den-mcp",
            ThreadId = 1,
        }, CancellationToken.None);

        Assert.NotNull(snapshot.ThreadRoot);
        Assert.Equal(1, snapshot.ThreadRoot.Id);
        Assert.Equal("Thread root", snapshot.ThreadRoot.Content);
        Assert.Equal(1092, snapshot.ThreadRoot.TaskId);

        // Second request should use single-message endpoint
        Assert.Contains(handler.Requests, r => r.Uri.EndsWith("/messages/1", StringComparison.Ordinal));
    }

    private static MessagesProjectionService CreateService(HttpMessageHandler handler)
    {
        return new MessagesProjectionService(
            new DenHttpClient(new HttpClient(handler)),
            _ => Task.FromResult(OperatorSettings.CreateDefault(() => "desktop-fixture") with { DenBaseUrl = "http://den.test" }),
            () => new DateTimeOffset(2026, 4, 30, 4, 0, 0, TimeSpan.Zero));
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private static HttpResponseMessage JsonResponseWithError(HttpStatusCode statusCode)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("{\"error\":\"internal\"}", Encoding.UTF8, "application/json"),
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
