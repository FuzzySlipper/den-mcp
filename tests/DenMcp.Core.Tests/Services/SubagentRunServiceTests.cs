using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using DenMcp.Core.Services;

namespace DenMcp.Core.Tests.Services;

public sealed class SubagentRunServiceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"den-subagent-runs-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    [Fact]
    public async Task SessionFallback_DoesNotTreatDisabledProviderSummaryAsRawPreview()
    {
        const string runId = "run-summary-fallback";
        const string summary = "Reviewed the safe provider-visible summary.";
        var artifactDir = Path.Combine(_tempRoot, "den-subagent-runs", runId);
        var sessionDir = Path.Combine(artifactDir, "sessions");
        Directory.CreateDirectory(sessionDir);

        await File.WriteAllTextAsync(Path.Combine(artifactDir, "status.json"), JsonSerializer.Serialize(new
        {
            reasoning_capture = new
            {
                capture_provider_summaries = false,
                capture_raw_local_previews = true,
                preview_chars = 240
            }
        }));
        await File.WriteAllTextAsync(Path.Combine(sessionDir, "session.jsonl"), JsonSerializer.Serialize(new
        {
            type = "message",
            timestamp = "2026-04-27T02:00:00Z",
            message = new
            {
                role = "assistant",
                provider = "openai",
                model = "gpt-test",
                content = new object[]
                {
                    new
                    {
                        type = "thinking",
                        thinking = summary,
                        thinkingSignature = JsonSerializer.Serialize(new { summary })
                    }
                }
            }
        }) + "\n");

        var stream = new FakeAgentStreamRepository([
            StreamEntry(runId, artifactDir)
        ]);
        var service = new SubagentRunService(stream, new FakeAgentRunRepository());

        var detail = await service.GetAsync(runId, new SubagentRunListOptions { ProjectId = "den-mcp", TaskId = 854 });

        Assert.NotNull(detail);
        var reasoning = Assert.Single(detail.WorkEvents, EventTypeIs("subagent.work_reasoning_end"));
        Assert.True(reasoning.TryGetProperty("reasoning_redacted", out var redacted));
        Assert.True(redacted.GetBoolean());
        Assert.False(reasoning.TryGetProperty("text_preview", out _));
        Assert.False(reasoning.TryGetProperty("reasoning_summary_preview", out _));
    }

    private static Predicate<JsonElement> EventTypeIs(string eventType) => element =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty("type", out var type) &&
        type.ValueKind == JsonValueKind.String &&
        type.GetString() == eventType;

    private static AgentStreamEntry StreamEntry(string runId, string artifactDir) => new()
    {
        Id = 1,
        StreamKind = AgentStreamKind.Ops,
        EventType = "subagent_completed",
        ProjectId = "den-mcp",
        TaskId = 854,
        Sender = "pi",
        DeliveryMode = AgentStreamDeliveryMode.RecordOnly,
        CreatedAt = DateTime.UtcNow,
        Metadata = JsonSerializer.SerializeToElement(new
        {
            run_id = runId,
            role = "coder",
            backend = "pi-cli",
            model = "gpt-test",
            artifacts = new { dir = artifactDir }
        })
    };

    private sealed class FakeAgentStreamRepository(IReadOnlyList<AgentStreamEntry> entries) : IAgentStreamRepository
    {
        public Task<AgentStreamEntry> AppendAsync(AgentStreamEntry entry) => Task.FromResult(entry);

        public Task<AgentStreamEntry?> GetByIdAsync(int id) => Task.FromResult(entries.FirstOrDefault(entry => entry.Id == id));

        public Task<List<AgentStreamEntry>> ListAsync(AgentStreamListOptions? options = null) =>
            Task.FromResult(entries
                .Where(entry => options?.MetadataRunId is null || entry.Metadata?.TryGetProperty("run_id", out var runId) == true && runId.GetString() == options.MetadataRunId)
                .ToList());
    }

    private sealed class FakeAgentRunRepository : IAgentRunRepository
    {
        public Task<bool> UpsertFromStreamEntryAsync(AgentStreamEntry entry) => Task.FromResult(false);
        public Task<AgentRunRecord?> RebuildFromStreamAsync(string runId) => Task.FromResult<AgentRunRecord?>(null);
        public Task<AgentRunRecord?> GetAsync(string runId, SubagentRunListOptions options) => Task.FromResult<AgentRunRecord?>(null);
        public Task<List<AgentRunRecord>> ListAsync(SubagentRunListOptions options) => Task.FromResult(new List<AgentRunRecord>());
    }
}
