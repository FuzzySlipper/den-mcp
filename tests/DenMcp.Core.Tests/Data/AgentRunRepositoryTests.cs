using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using DenMcp.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace DenMcp.Core.Tests.Data;

public class AgentRunRepositoryTests : IAsyncLifetime
{
    private readonly TestDb _testDb = new();
    private AgentStreamRepository _stream = null!;
    private AgentRunRepository _runs = null!;
    private ProjectTask _task = null!;

    public async Task InitializeAsync()
    {
        await _testDb.InitializeAsync();
        _stream = new AgentStreamRepository(_testDb.Db);
        _runs = new AgentRunRepository(_testDb.Db);

        var projects = new ProjectRepository(_testDb.Db);
        await projects.CreateAsync(new Project { Id = "proj", Name = "Project" });
        await projects.CreateAsync(new Project { Id = "other", Name = "Other" });

        var tasks = new TaskRepository(_testDb.Db);
        _task = await tasks.CreateAsync(new ProjectTask
        {
            ProjectId = "proj",
            Title = "Sub-agent host"
        });
    }

    public Task DisposeAsync() => _testDb.DisposeAsync();

    [Fact]
    public async Task UpsertFromStreamEntry_ProjectsLifecycleIntoDurableRunRecord()
    {
        var started = await AppendAndProjectAsync("subagent_started", """
            {
              "schema":"den_subagent_run",
              "schema_version":1,
              "run_id":"run-complete",
              "role":"coder",
              "backend":"pi-cli",
              "model":"gpt-test",
              "sender_instance_id":"pi-proj-coder-run-complete",
              "started_at":"2026-04-26T08:00:00Z",
              "artifacts":{
                "dir":"/tmp/den-subagent-runs/run-complete",
                "stdout_jsonl_path":"/tmp/den-subagent-runs/run-complete/stdout.jsonl",
                "stderr_log_path":"/tmp/den-subagent-runs/run-complete/stderr.log",
                "status_json_path":"/tmp/den-subagent-runs/run-complete/status.json",
                "events_jsonl_path":"/tmp/den-subagent-runs/run-complete/events.jsonl"
              }
            }
            """);
        var processStarted = await AppendAndProjectAsync("subagent_process_started", """
            {"run_id":"run-complete","event":{"type":"subagent.process_started","pid":4242}}
            """);
        await AppendAndProjectAsync("subagent_heartbeat", """
            {"run_id":"run-complete","event":{"type":"subagent.heartbeat","duration_ms":5000}}
            """);
        await AppendAndProjectAsync("subagent_assistant_output", """
            {"run_id":"run-complete","event":{"type":"subagent.assistant_output","chars":32}}
            """);
        var completed = await AppendAndProjectAsync("subagent_completed", """
            {
              "run_id":"run-complete",
              "role":"coder",
              "backend":"pi-cli",
              "model":"gpt-test-final",
              "ended_at":"2026-04-26T08:00:09Z",
              "duration_ms":9000,
              "exit_code":0,
              "output_status":"assistant_final",
              "infrastructure_warning_reason":"extension_runtime"
            }
            """);

        var record = await _runs.GetAsync("run-complete", new SubagentRunListOptions { ProjectId = "proj", TaskId = _task.Id });

        Assert.NotNull(record);
        Assert.Equal("run-complete", record!.RunId);
        Assert.Equal("complete", record.State);
        Assert.Equal("proj", record.ProjectId);
        Assert.Equal(_task.Id, record.TaskId);
        Assert.Equal("coder", record.Role);
        Assert.Equal("pi-cli", record.Backend);
        Assert.Equal("gpt-test-final", record.Model);
        Assert.Equal("pi-proj-coder-run-complete", record.SenderInstanceId);
        Assert.Equal(DateTime.Parse("2026-04-26T08:00:00Z"), record.StartedAt);
        Assert.Equal(DateTime.Parse("2026-04-26T08:00:09Z"), record.EndedAt);
        Assert.Equal(9000, record.DurationMs);
        Assert.Equal(4242, record.Pid);
        Assert.Equal(0, record.ExitCode);
        Assert.Equal("assistant_final", record.OutputStatus);
        Assert.Equal("extension_runtime", record.InfrastructureWarningReason);
        Assert.Equal("/tmp/den-subagent-runs/run-complete", record.ArtifactDir);
        Assert.Equal("/tmp/den-subagent-runs/run-complete/stdout.jsonl", record.StdoutJsonlPath);
        Assert.Equal(started.Id, record.StartedStreamEntryId);
        Assert.Equal(completed.Id, record.LatestStreamEntryId);
        Assert.Equal(1, record.HeartbeatCount);
        Assert.Equal(1, record.AssistantOutputCount);
        Assert.Equal(5, record.EventCount);
        Assert.NotNull(record.LastHeartbeatAt);
        Assert.NotNull(record.LastAssistantOutputAt);

        // Re-projecting an existing stream row is idempotent: counts are rebuilt
        // from agent_stream_entries instead of incremented in-place.
        Assert.True(await _runs.UpsertFromStreamEntryAsync(completed));
        var idempotent = await _runs.GetAsync("run-complete", new SubagentRunListOptions());
        Assert.Equal(5, idempotent!.EventCount);
        Assert.Equal(1, idempotent.HeartbeatCount);
        Assert.Equal(completed.Id, idempotent.LatestStreamEntryId);
        Assert.NotEqual(processStarted.Id, idempotent.LatestStreamEntryId);
    }

    [Fact]
    public async Task AgentStreamOpsService_AppendsAuditEntryAndProjectsAgentRun()
    {
        var ops = new AgentStreamOpsService(_stream, NullLogger<AgentStreamOpsService>.Instance, _runs);

        var entry = await ops.AppendOpsAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = "subagent_started",
            ProjectId = "proj",
            TaskId = _task.Id,
            Sender = "pi",
            SenderInstanceId = "pi-proj-coder-run-ops",
            DeliveryMode = AgentStreamDeliveryMode.RecordOnly,
            Metadata = Metadata("""{"run_id":"run-ops","role":"coder","backend":"pi-cli"}""")
        });

        var record = await _runs.GetAsync("run-ops", new SubagentRunListOptions { ProjectId = "proj" });

        Assert.NotNull(record);
        Assert.Equal(entry.Id, record!.LatestStreamEntryId);
        Assert.Equal(entry.Id, record.StartedStreamEntryId);
        Assert.Equal("running", record.State);
        Assert.Equal("coder", record.Role);
    }

    [Fact]
    public async Task RebuildFromStreamAsync_BackfillsAndListFiltersDurableRecords()
    {
        await AppendAsync("subagent_started", "run-timeout", "planner", """{"run_id":"run-timeout","role":"planner","backend":"pi-cli"}""");
        await AppendAsync("subagent_timeout", "run-timeout", "planner", """{"run_id":"run-timeout","role":"planner","timeout_kind":"startup","exit_code":143,"output_status":"no_assistant_final","infrastructure_failure_reason":"timeout"}""");

        await AppendAsync("subagent_started", "run-abort", "reviewer", """{"run_id":"run-abort","role":"reviewer","backend":"pi-cli"}""");
        await AppendAsync("subagent_abort_requested", "run-abort", "reviewer", """{"run_id":"run-abort","role":"reviewer","action":"abort"}""");
        await AppendAsync("subagent_aborted", "run-abort", "reviewer", """{"run_id":"run-abort","role":"reviewer","aborted":true,"exit_code":143}""");

        var timeout = await _runs.RebuildFromStreamAsync("run-timeout");
        var abort = await _runs.RebuildFromStreamAsync("run-abort");

        Assert.Equal("timeout", timeout!.State);
        Assert.Equal("startup", timeout.TimeoutKind);
        Assert.Equal("timeout", timeout.InfrastructureFailureReason);
        Assert.Equal("aborted", abort!.State);
        Assert.Equal(3, abort.EventCount);

        var problemRuns = await _runs.ListAsync(new SubagentRunListOptions
        {
            ProjectId = "proj",
            State = "problem",
            Limit = 10
        });

        Assert.Equal(new[] { "run-abort", "run-timeout" }, problemRuns.Select(run => run.RunId).Order().ToArray());
        Assert.Empty(await _runs.ListAsync(new SubagentRunListOptions { ProjectId = "other", Limit = 10 }));
        Assert.Empty(await _runs.ListAsync(new SubagentRunListOptions { ProjectId = "proj", State = "complete", Limit = 10 }));
    }

    private async Task<AgentStreamEntry> AppendAndProjectAsync(string eventType, string metadataJson)
    {
        var entry = await AppendAsync(eventType, "run-complete", "coder", metadataJson);
        Assert.True(await _runs.UpsertFromStreamEntryAsync(entry));
        return entry;
    }

    private Task<AgentStreamEntry> AppendAsync(string eventType, string runId, string role, string metadataJson) =>
        _stream.AppendAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = eventType,
            ProjectId = "proj",
            TaskId = _task.Id,
            Sender = "pi",
            SenderInstanceId = $"pi-proj-{role}-{runId}",
            DeliveryMode = AgentStreamDeliveryMode.RecordOnly,
            Body = $"{role} {eventType}",
            Metadata = Metadata(metadataJson)
        });

    private static JsonElement Metadata(string json) => JsonSerializer.Deserialize<JsonElement>(json);
}
