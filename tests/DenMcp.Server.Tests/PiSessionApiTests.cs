using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Llm;
using DenMcp.Core.Models;
using DenMcp.Core.Services;
using DenMcp.Server.Tools;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DenMcp.Server.Tests;

public sealed class PiSessionApiTests : IAsyncLifetime
{
    private const string ProjectId = "den-mcp";
    private PiSessionAppFactory _factory = null!;
    private HttpClient _client = null!;
    private ProjectTask _task = null!;

    public async Task InitializeAsync()
    {
        _factory = new PiSessionAppFactory();
        _client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        var tasks = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        await projects.CreateAsync(new Project { Id = ProjectId, Name = "Den MCP" });
        _task = await tasks.CreateAsync(new ProjectTask
        {
            ProjectId = ProjectId,
            Title = "Launch pi",
            Status = DenMcp.Core.Models.TaskStatus.InProgress,
        });
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task StagedSmoke_CoderReviewerFullLoop_UsesDenStateAndBoundedReferences()
    {
        using var scope = _factory.Services.CreateScope();
        var tasks = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        var messages = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
        var service = scope.ServiceProvider.GetRequiredService<IPiSessionService>();
        var sessions = scope.ServiceProvider.GetRequiredService<IPiSessionRepository>();

        var longDescription = new string('z', 5000);
        var smokeTask = await tasks.CreateAsync(new ProjectTask
        {
            ProjectId = ProjectId,
            Title = "Smoke Den Pi worker loop",
            Description = longDescription,
            Status = DenMcp.Core.Models.TaskStatus.InProgress,
            Tags = ["smoke-test"]
        });

        // Stage 1: coder-only smoke: packet -> coder launch -> implementation packet -> branch/head/test verification.
        var coderStart = await CoderPathTools.StartCoderWorkerPath(
            tasks,
            messages,
            service,
            ProjectId,
            smokeTask.Id,
            requested_by: "hermes",
            branch: "task/1247-smoke-coder",
            base_branch: "main",
            base_commit: "base-smoke",
            session_id: "smoke-coder",
            run_id: "smoke-coder-run",
            callback_ports: "[{\"host_port\":21467,\"container_port\":1455}]",
            verbose: true);
        using var coderStartJson = JsonDocument.Parse(coderStart);
        var coderWorker = coderStartJson.RootElement.GetProperty("worker_run");
        var coderPacketId = coderStartJson.RootElement.GetProperty("packet_ref").GetProperty("message_id").GetInt32();
        Assert.Equal("coder", coderWorker.GetProperty("role").GetString());
        Assert.False(coderWorker.GetProperty("prompt_ref").GetRawText().Contains(longDescription, StringComparison.Ordinal));
        Assert.False(_factory.FakeHost.Launches.Last().Record.LaunchCommandDisplay.Contains(longDescription, StringComparison.Ordinal));
        Assert.False(string.IsNullOrWhiteSpace(coderWorker.GetProperty("session").GetProperty("tmux_session").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(coderWorker.GetProperty("session").GetProperty("container_name").GetString()));

        await CompletionTools.PostWorkerCompletionPacket(
            service,
            sessions,
            messages,
            ProjectId,
            run_id: "smoke-coder-run",
            requested_by: "pi-coder",
            status: "completed",
            role: "coder",
            packet_type: "implementation_packet",
            summary: "Smoke implementation completed.",
            branch: "task/1247-smoke-coder",
            head_commit: "smoke-head-coder",
            tests_run: "[\"dotnet build tests/DenMcp.Server.Tests/DenMcp.Server.Tests.csproj --no-restore\"]",
            verbose: true);
        var coderVerify = await CoderPathTools.VerifyCoderWorkerCompletion(messages, ProjectId, run_id: "smoke-coder-run", task_id: smokeTask.Id, verbose: true);
        using var coderVerifyJson = JsonDocument.Parse(coderVerify);
        Assert.Equal("ready_for_review", coderVerifyJson.RootElement.GetProperty("verdict").GetString());

        // Stage 2: reviewer smoke: implementation/review context -> reviewer launch -> findings/verdict packet.
        var reviewerStart = await ReviewerPathTools.StartReviewerWorkerPath(
            tasks,
            messages,
            service,
            ProjectId,
            smokeTask.Id,
            requested_by: "hermes",
            review_round_id: 247,
            branch: "task/1247-smoke-coder",
            base_branch: "main",
            base_commit: "base-smoke",
            head_commit: "smoke-head-coder",
            session_id: "smoke-reviewer",
            run_id: "smoke-reviewer-run",
            callback_ports: "[{\"host_port\":21468,\"container_port\":1455}]",
            verbose: true);
        using var reviewerStartJson = JsonDocument.Parse(reviewerStart);
        Assert.Equal("reviewer", reviewerStartJson.RootElement.GetProperty("worker_run").GetProperty("role").GetString());

        await CompletionTools.PostWorkerCompletionPacket(
            service,
            sessions,
            messages,
            ProjectId,
            run_id: "smoke-reviewer-run",
            requested_by: "den-mcp-runner-reviewer",
            status: "completed",
            role: "reviewer",
            packet_type: "review_findings_packet",
            summary: "Smoke reviewer verdict: looks_good.",
            branch: "task/1247-smoke-coder",
            head_commit: "smoke-head-coder",
            review_round_id: 247,
            finding_ids: "[]",
            tests_run: "[\"dotnet build tests/DenMcp.Server.Tests/DenMcp.Server.Tests.csproj --no-restore\"]",
            verbose: true);
        var reviewerVerify = await ReviewerPathTools.VerifyReviewerWorkerCompletion(messages, ProjectId, run_id: "smoke-reviewer-run", task_id: smokeTask.Id, verbose: true);
        using var reviewerVerifyJson = JsonDocument.Parse(reviewerVerify);
        Assert.Equal("review_recorded", reviewerVerifyJson.RootElement.GetProperty("verdict").GetString());

        // Stage 3: full-loop smoke: orchestrator next-action decision + cleanup/status handles.
        var coderStatus = await WorkerTools.GetWorkerRunStatus(service, messages, ProjectId, "smoke-coder-run", task_id: smokeTask.Id, verbose: true);
        var reviewerStatus = await WorkerTools.GetWorkerRunStatus(service, messages, ProjectId, "smoke-reviewer-run", task_id: smokeTask.Id, verbose: true);
        using var coderStatusJson = JsonDocument.Parse(coderStatus);
        using var reviewerStatusJson = JsonDocument.Parse(reviewerStatus);
        Assert.Equal("running", coderStatusJson.RootElement.GetProperty("worker_run").GetProperty("status").GetString());
        Assert.Equal("running", reviewerStatusJson.RootElement.GetProperty("worker_run").GetProperty("status").GetString());
        Assert.Equal("posted_completed", coderStatusJson.RootElement.GetProperty("reconciliation").GetProperty("completion_state").GetString());
        Assert.Equal("posted_completed", reviewerStatusJson.RootElement.GetProperty("reconciliation").GetProperty("completion_state").GetString());
        Assert.Equal("request_review_or_mark_done", DecideNextAction(coderVerifyJson.RootElement.GetProperty("verdict").GetString(), reviewerVerifyJson.RootElement.GetProperty("verdict").GetString()));

        var coderPacket = await messages.GetByIdAsync(coderPacketId);
        Assert.Equal("coder_context_packet", coderPacket!.Metadata?.GetProperty("type").GetString());
        Assert.DoesNotContain(longDescription, _factory.FakeHost.Launches[0].Record.LaunchCommandDisplay, StringComparison.Ordinal);
        Assert.True(_factory.FakeHost.Launches.Count >= 2);
    }

    private static string DecideNextAction(string? coderVerdict, string? reviewerVerdict) =>
        coderVerdict == "ready_for_review" && reviewerVerdict == "review_recorded"
            ? "request_review_or_mark_done"
            : "wait_or_recover";

    [Fact]
    public async Task ReviewerPathTools_StartAndVerify_UseDenReviewCompletionState()
    {
        using var scope = _factory.Services.CreateScope();
        var tasks = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        var messages = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
        var service = scope.ServiceProvider.GetRequiredService<IPiSessionService>();
        var sessions = scope.ServiceProvider.GetRequiredService<IPiSessionRepository>();

        var started = await ReviewerPathTools.StartReviewerWorkerPath(
            tasks,
            messages,
            service,
            ProjectId,
            _task.Id,
            requested_by: "hermes",
            review_round_id: 125,
            branch: "task/1243-reviewer-worker-path",
            base_branch: "main",
            base_commit: "base1243",
            head_commit: "head1243",
            session_id: "reviewer-path-a",
            run_id: "reviewer-path-run-a",
            callback_ports: "[{\"host_port\":21466,\"container_port\":1455}]",
            verbose: true);
        using var startedJson = JsonDocument.Parse(started);
        Assert.Equal("launched", startedJson.RootElement.GetProperty("path_state").GetString());
        Assert.Equal("reviewer", startedJson.RootElement.GetProperty("worker_run").GetProperty("role").GetString());
        Assert.Equal("den-mcp-runner-reviewer", startedJson.RootElement.GetProperty("reviewer_identity").GetString());

        await CompletionTools.PostWorkerCompletionPacket(
            service,
            sessions,
            messages,
            ProjectId,
            run_id: "reviewer-path-run-a",
            requested_by: "den-mcp-runner-reviewer",
            status: "completed",
            role: "reviewer",
            packet_type: "review_findings_packet",
            summary: "Looks good.",
            branch: "task/1243-reviewer-worker-path",
            head_commit: "head1243",
            review_round_id: 125,
            tests_run: "[\"dotnet build\"]",
            verbose: true);

        var verified = await ReviewerPathTools.VerifyReviewerWorkerCompletion(messages, ProjectId, run_id: "reviewer-path-run-a", task_id: _task.Id, verbose: true);
        using var verifiedJson = JsonDocument.Parse(verified);
        Assert.Equal("review_recorded", verifiedJson.RootElement.GetProperty("verdict").GetString());
        Assert.True(verifiedJson.RootElement.GetProperty("checks").GetProperty("review_findings_packet_exists").GetBoolean());
        Assert.True(verifiedJson.RootElement.GetProperty("checks").GetProperty("review_round_id_reported").GetBoolean());
    }

    [Fact]
    public async Task CoderPathTools_StartAndVerify_UseDenPacketLaunchAndCompletionState()
    {
        using var scope = _factory.Services.CreateScope();
        var tasks = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        var messages = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
        var service = scope.ServiceProvider.GetRequiredService<IPiSessionService>();
        var sessions = scope.ServiceProvider.GetRequiredService<IPiSessionRepository>();

        var started = await CoderPathTools.StartCoderWorkerPath(
            tasks,
            messages,
            service,
            ProjectId,
            _task.Id,
            requested_by: "hermes",
            branch: "task/1242-coder-worker-path",
            base_branch: "main",
            base_commit: "base1242",
            session_id: "coder-path-a",
            run_id: "coder-path-run-a",
            callback_ports: "[{\"host_port\":21465,\"container_port\":1455}]",
            verbose: true);
        using var startedJson = JsonDocument.Parse(started);
        Assert.Equal("launched", startedJson.RootElement.GetProperty("path_state").GetString());
        Assert.Equal("coder", startedJson.RootElement.GetProperty("worker_run").GetProperty("role").GetString());

        var incomplete = await CoderPathTools.VerifyCoderWorkerCompletion(messages, ProjectId, run_id: "coder-path-run-a", task_id: _task.Id, verbose: true);
        using var incompleteJson = JsonDocument.Parse(incomplete);
        Assert.Equal("incomplete", incompleteJson.RootElement.GetProperty("verdict").GetString());
        Assert.Equal("missing_packet", incompleteJson.RootElement.GetProperty("completion_state").GetString());

        await CompletionTools.PostWorkerCompletionPacket(
            service,
            sessions,
            messages,
            ProjectId,
            run_id: "coder-path-run-a",
            requested_by: "pi-worker",
            status: "completed",
            role: "coder",
            packet_type: "implementation_packet",
            summary: "Done",
            branch: "task/1242-coder-worker-path",
            head_commit: "head1242",
            tests_run: "[\"dotnet build\"]",
            verbose: true);

        var verified = await CoderPathTools.VerifyCoderWorkerCompletion(messages, ProjectId, run_id: "coder-path-run-a", task_id: _task.Id, verbose: true);
        using var verifiedJson = JsonDocument.Parse(verified);
        Assert.Equal("ready_for_review", verifiedJson.RootElement.GetProperty("verdict").GetString());
        Assert.True(verifiedJson.RootElement.GetProperty("checks").GetProperty("implementation_packet_exists").GetBoolean());
        Assert.True(verifiedJson.RootElement.GetProperty("checks").GetProperty("head_commit_reported").GetBoolean());
    }

    [Fact]
    public async Task RoleWorkerTools_LaunchCoderWorker_PreparesPacketAndLaunchesCoderRole()
    {
        using var scope = _factory.Services.CreateScope();
        var tasks = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        var messages = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
        var service = scope.ServiceProvider.GetRequiredService<IPiSessionService>();

        var json = await RoleWorkerTools.LaunchCoderWorker(
            tasks,
            messages,
            service,
            ProjectId,
            _task.Id,
            requested_by: "hermes",
            branch: "task/1255-role-worker-launch-adapters",
            base_branch: "main",
            base_commit: "base1255",
            allowed_scope: "src/DenMcp.Server/Tools",
            session_id: "coder-role-a",
            run_id: "coder-run-a",
            callback_ports: "[{\"host_port\":21463,\"container_port\":1455}]",
            verbose: true);

        using var doc = JsonDocument.Parse(json);
        var worker = doc.RootElement.GetProperty("worker_run");
        Assert.Equal("coder", worker.GetProperty("role").GetString());
        Assert.Equal("coder", worker.GetProperty("worker_identity").GetString());
        Assert.Equal("running", worker.GetProperty("status").GetString());
        Assert.Equal("coder-run-a", worker.GetProperty("run_id").GetString());
        var packetRef = worker.GetProperty("prompt_ref").GetProperty("message_id").GetInt32();
        Assert.True(packetRef > 0);
        Assert.Equal(packetRef, doc.RootElement.GetProperty("packet_ref").GetProperty("message_id").GetInt32());
        Assert.Equal("coder_context_packet", doc.RootElement.GetProperty("packet_ref").GetProperty("packet_type").GetString());
        Assert.Equal("task/1255-role-worker-launch-adapters", worker.GetProperty("requested_repo").GetProperty("branch").GetString());

        var packet = await messages.GetByIdAsync(packetRef);
        Assert.NotNull(packet);
        Assert.Equal("coder_context_packet", packet!.Metadata?.GetProperty("type").GetString());
    }

    [Fact]
    public async Task RoleWorkerTools_LaunchReviewerWorker_PreparesReviewerPacketAndRoleDefaults()
    {
        using var scope = _factory.Services.CreateScope();
        var tasks = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        var messages = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
        var service = scope.ServiceProvider.GetRequiredService<IPiSessionService>();

        var json = await RoleWorkerTools.LaunchReviewerWorker(
            tasks,
            messages,
            service,
            ProjectId,
            _task.Id,
            requested_by: "hermes",
            review_round_id: 42,
            branch: "task/review-target",
            base_branch: "main",
            head_commit: "head1255",
            session_id: "reviewer-role-a",
            run_id: "reviewer-run-a",
            callback_ports: "[{\"host_port\":21464,\"container_port\":1455}]",
            verbose: true);

        using var doc = JsonDocument.Parse(json);
        var worker = doc.RootElement.GetProperty("worker_run");
        Assert.Equal("reviewer", worker.GetProperty("role").GetString());
        Assert.Equal("reviewer", worker.GetProperty("worker_identity").GetString());
        Assert.Equal("reviewer-run-a", worker.GetProperty("run_id").GetString());
        Assert.Equal("reviewer_context_packet", doc.RootElement.GetProperty("packet_ref").GetProperty("packet_type").GetString());
        Assert.Equal(42, doc.RootElement.GetProperty("packet_ref").GetProperty("review_round_id").GetInt32());
        Assert.Contains(worker.GetProperty("artifact_handles").EnumerateArray(), h => h.GetProperty("name").GetString() == "status");
    }

    [Fact]
    public async Task LaunchWorkerTool_CreatesIdempotentRawWorkerRunWithContractMetadata()
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IPiSessionService>();

        var first = await WorkerTools.LaunchPiWorker(service,
            project_id: ProjectId,
            requested_by: "hermes",
            role: "raw",
            task_id: _task.Id,
            prompt_packet_message_id: 12345,
            workspace_id: "workspace-worker",
            branch: "task/1239-worker-tools",
            base_branch: "main",
            base_commit: "base123",
            model_hint: "test-model",
            session_mode: "fresh",
            timeout_seconds: 3600,
            dedupe_key: "worker-dedupe-a",
            callback_ports: "[{\"host_port\":21459,\"container_port\":1455}]",
            verbose: true);

        using var firstJson = JsonDocument.Parse(first);
        var worker = firstJson.RootElement.GetProperty("worker_run");
        Assert.Equal("created", firstJson.RootElement.GetProperty("idempotency").GetProperty("status").GetString());
        Assert.Equal("raw", worker.GetProperty("role").GetString());
        Assert.Equal("raw", worker.GetProperty("worker_identity").GetString());
        Assert.Equal("running", worker.GetProperty("status").GetString());
        Assert.Equal("running", worker.GetProperty("state").GetString());
        Assert.Equal("fresh", worker.GetProperty("session_mode").GetString());
        Assert.Equal("task/1239-worker-tools", worker.GetProperty("requested_repo").GetProperty("branch").GetString());
        Assert.Equal("base123", worker.GetProperty("requested_repo").GetProperty("base_commit").GetString());
        Assert.Equal("host-test", worker.GetProperty("session").GetProperty("host_id").GetString());
        Assert.False(string.IsNullOrWhiteSpace(worker.GetProperty("session").GetProperty("tmux_session").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(worker.GetProperty("session").GetProperty("container_name").GetString()));
        Assert.Contains(worker.GetProperty("artifact_handles").EnumerateArray(), h => h.GetProperty("name").GetString() == "status");
        Assert.Equal(12345, worker.GetProperty("prompt_ref").GetProperty("message_id").GetInt32());

        var second = await WorkerTools.LaunchPiWorker(service,
            project_id: ProjectId,
            requested_by: "hermes",
            role: "raw",
            task_id: _task.Id,
            prompt_packet_message_id: 12345,
            dedupe_key: "worker-dedupe-a",
            callback_ports: "[{\"host_port\":21459,\"container_port\":1455}]",
            verbose: true);

        using var secondJson = JsonDocument.Parse(second);
        Assert.Equal("existing", secondJson.RootElement.GetProperty("idempotency").GetProperty("status").GetString());
        Assert.Equal(worker.GetProperty("run_id").GetString(), secondJson.RootElement.GetProperty("worker_run").GetProperty("run_id").GetString());
        Assert.Single(_factory.FakeHost.Launches);
    }

    [Fact]
    public async Task WorkerTools_QueryAbortAndRerun_SurfaceRawLifecycleControls()
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IPiSessionService>();

        var launched = await WorkerTools.LaunchPiWorker(service,
            project_id: ProjectId,
            requested_by: "hermes",
            role: "raw",
            task_id: _task.Id,
            prompt_packet_message_id: 12346,
            session_id: "worker-control-a",
            dedupe_key: "worker-control-a",
            callback_ports: "[{\"host_port\":21460,\"container_port\":1455}]",
            verbose: true);
        using var launchedJson = JsonDocument.Parse(launched);
        var runId = launchedJson.RootElement.GetProperty("worker_run").GetProperty("run_id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(runId));

        var get = await WorkerTools.GetWorkerRun(service, ProjectId, runId!, verbose: true);
        using var getJson = JsonDocument.Parse(get);
        Assert.Equal("worker-control-a", getJson.RootElement.GetProperty("worker_run").GetProperty("session_id").GetString());

        var list = await WorkerTools.ListWorkerRuns(service, ProjectId, task_id: _task.Id, role: "raw", verbose: true);
        using var listJson = JsonDocument.Parse(list);
        Assert.Contains(listJson.RootElement.GetProperty("worker_runs").EnumerateArray(), r => r.GetProperty("run_id").GetString() == runId);

        var aborted = await WorkerTools.AbortWorkerRun(service, ProjectId, runId!, requested_by: "hermes", reason: "test abort", verbose: true);
        using var abortedJson = JsonDocument.Parse(aborted);
        Assert.Equal("aborted", abortedJson.RootElement.GetProperty("worker_run").GetProperty("status").GetString());
        Assert.Equal("aborted", abortedJson.RootElement.GetProperty("worker_run").GetProperty("failure_category").GetString());

        var rerun = await WorkerTools.RerunWorkerRun(service, ProjectId, runId!, requested_by: "hermes", reason: "test rerun", verbose: true);
        using var rerunJson = JsonDocument.Parse(rerun);
        Assert.Equal("created", rerunJson.RootElement.GetProperty("idempotency").GetProperty("status").GetString());
        var rerunWorker = rerunJson.RootElement.GetProperty("worker_run");
        Assert.NotEqual(runId, rerunWorker.GetProperty("run_id").GetString());
        Assert.Equal(runId, rerunWorker.GetProperty("rerun_of_run_id").GetString());
    }

    [Fact]
    public async Task CompletionTools_PostCompletion_IsIdempotentAndUpdatesWorkerStatus()
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IPiSessionService>();
        var sessions = scope.ServiceProvider.GetRequiredService<IPiSessionRepository>();
        var messages = scope.ServiceProvider.GetRequiredService<IMessageRepository>();

        var launched = await WorkerTools.LaunchPiWorker(service,
            project_id: ProjectId,
            requested_by: "hermes",
            role: "coder",
            task_id: _task.Id,
            prompt_packet_message_id: 1241,
            session_id: "worker-complete-a",
            run_id: "run-complete-a",
            callback_ports: "[{\"host_port\":21461,\"container_port\":1455}]",
            verbose: true);
        using var launchedJson = JsonDocument.Parse(launched);
        Assert.Equal("running", launchedJson.RootElement.GetProperty("worker_run").GetProperty("status").GetString());

        var completion = await CompletionTools.PostWorkerCompletionPacket(
            service,
            sessions,
            messages,
            ProjectId,
            run_id: "run-complete-a",
            requested_by: "pi-worker",
            status: "completed",
            role: "coder",
            packet_type: "implementation_packet",
            summary: "Implemented the packet flow.",
            branch: "task/1241-worker-completion-status",
            head_commit: "abc999",
            tests_run: "[\"dotnet build\"]",
            dedupe_key: "completion-a",
            verbose: true);
        using var completionJson = JsonDocument.Parse(completion);
        var packet = completionJson.RootElement.GetProperty("completion");
        Assert.Equal("created", completionJson.RootElement.GetProperty("idempotency").GetProperty("status").GetString());
        Assert.Equal("completed", packet.GetProperty("status").GetString());
        Assert.Equal("implementation_packet", packet.GetProperty("packet_type").GetString());
        Assert.Equal("abc999", packet.GetProperty("final_repo").GetProperty("head_commit").GetString());
        var messageId = packet.GetProperty("message_id").GetInt32();

        var duplicate = await CompletionTools.PostWorkerCompletionPacket(
            service,
            sessions,
            messages,
            ProjectId,
            run_id: "run-complete-a",
            requested_by: "pi-worker",
            status: "completed",
            role: "coder",
            packet_type: "implementation_packet",
            summary: "Duplicate should return existing.",
            dedupe_key: "completion-a",
            verbose: true);
        using var duplicateJson = JsonDocument.Parse(duplicate);
        Assert.Equal("existing", duplicateJson.RootElement.GetProperty("idempotency").GetProperty("status").GetString());
        Assert.Equal(messageId, duplicateJson.RootElement.GetProperty("completion").GetProperty("message_id").GetInt32());

        var worker = await WorkerTools.GetWorkerRun(service, ProjectId, "run-complete-a", verbose: true);
        using var workerJson = JsonDocument.Parse(worker);
        Assert.Equal("running", workerJson.RootElement.GetProperty("worker_run").GetProperty("status").GetString());
        Assert.Equal("pending", workerJson.RootElement.GetProperty("worker_run").GetProperty("lifecycle").GetProperty("completion_packet_state").GetString());

        var status = await WorkerTools.GetWorkerRunStatus(service, messages, ProjectId, "run-complete-a", task_id: _task.Id, verbose: true);
        using var statusJson = JsonDocument.Parse(status);
        Assert.Equal(messageId, statusJson.RootElement.GetProperty("completion").GetProperty("message_id").GetInt32());
        Assert.Contains(statusJson.RootElement.GetProperty("reconciliation").GetProperty("diagnostics").EnumerateArray(), item => item.GetString()!.Contains("runtime still appears active", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompletionTools_GetLatestCompletion_ReportsMissingAndMalformedStates()
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IPiSessionService>();
        var sessions = scope.ServiceProvider.GetRequiredService<IPiSessionRepository>();
        var messages = scope.ServiceProvider.GetRequiredService<IMessageRepository>();

        await WorkerTools.LaunchPiWorker(service,
            project_id: ProjectId,
            requested_by: "hermes",
            role: "reviewer",
            task_id: _task.Id,
            prompt_packet_message_id: 1242,
            session_id: "worker-missing-a",
            run_id: "run-missing-a",
            callback_ports: "[{\"host_port\":21462,\"container_port\":1455}]",
            verbose: true);

        var missing = await CompletionTools.GetLatestWorkerCompletion(messages, ProjectId, run_id: "run-missing-a", task_id: _task.Id, role: "reviewer", verbose: true);
        using var missingJson = JsonDocument.Parse(missing);
        Assert.Equal("missing_packet", missingJson.RootElement.GetProperty("completion_state").GetString());

        var malformed = await CompletionTools.PostWorkerCompletionPacket(
            service,
            sessions,
            messages,
            ProjectId,
            run_id: "run-missing-a",
            requested_by: "pi-worker",
            status: "wat",
            role: "reviewer",
            packet_type: "review_findings_packet",
            summary: "Bad status",
            verbose: true);
        using var malformedJson = JsonDocument.Parse(malformed);
        Assert.Equal("malformed", malformedJson.RootElement.GetProperty("completion_state").GetString());
        Assert.Equal("malformed", malformedJson.RootElement.GetProperty("completion").GetProperty("status").GetString());
        Assert.Equal("malformed_packet", malformedJson.RootElement.GetProperty("completion").GetProperty("failure_category").GetString());
    }

    [Fact]
    public async Task Launch_List_Status_AndAttach_ReturnDurableSessionMetadata()
    {
        var response = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/pi-sessions", new
        {
            session_id = "session-a",
            task_id = _task.Id,
            workspace_id = "workspace-a",
            run_id = "run-a",
            title = "Pi coder",
            requested_by = "hermes",
            tool_profile = "coding",
            model = "openai-codex/gpt-5.5",
            provider = "openai-codex",
            callback_ports = new[]
            {
                new { host_port = 21455, container_port = 1455 },
            },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var createdJson = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var detail = createdJson.RootElement;
        var session = detail.GetProperty("session");
        Assert.Equal("session-a", session.GetProperty("session_id").GetString());
        Assert.Equal(ProjectId, session.GetProperty("project_id").GetString());
        Assert.Equal(_task.Id, session.GetProperty("task_id").GetInt32());
        Assert.Equal("workspace-a", session.GetProperty("workspace_id").GetString());
        Assert.Equal("run-a", session.GetProperty("run_id").GetString());
        Assert.Equal("host-test", session.GetProperty("host_id").GetString());
        Assert.Equal("running", session.GetProperty("state").GetString());
        Assert.False(string.IsNullOrWhiteSpace(session.GetProperty("tmux_session_name").GetString()));
        var launchCommand = session.GetProperty("launch_command").EnumerateArray().Select(v => v.GetString()).ToList();
        Assert.Contains(launchCommand, value => value is "docker" or "/usr/bin/docker");
        Assert.Contains("DOCKER_HOST=unix:///run/den-mcp/docker-rt/docker.sock", launchCommand);
        Assert.Contains("OPENAI_API_KEY=", launchCommand);
        Assert.DoesNotContain(launchCommand, value => value?.Contains("test-key", StringComparison.Ordinal) == true);
        Assert.Equal("pi_docker_compose", session.GetProperty("launch_profile_kind").GetString());
        Assert.Equal("coding", session.GetProperty("tool_profile").GetString());
        Assert.Equal("openai-codex/gpt-5.5", session.GetProperty("model").GetString());
        Assert.Equal("external_attach_info", detail.GetProperty("attach").GetProperty("mode").GetString());

        var fakeHost = _factory.FakeHost;
        Assert.Single(fakeHost.Launches);
        Assert.Equal("session-a", fakeHost.Launches[0].Record.SessionId);
        Assert.Equal("/srv/dev", fakeHost.Launches[0].LaunchProfile.DevDir);
        Assert.Equal("unix:///run/den-mcp/docker-rt/docker.sock", fakeHost.Launches[0].LaunchProfile.DockerHost);
        Assert.Equal("unix:///run/den-mcp/docker-rt/docker.sock", fakeHost.Launches[0].LaunchProfile.Environment["DOCKER_HOST"]);
        Assert.Equal(string.Empty, fakeHost.Launches[0].LaunchProfile.Environment["OPENAI_API_KEY"]);
        Assert.Contains("OPENAI_API_KEY", fakeHost.Launches[0].LaunchProfile.ScrubbedEnvironmentVariables);

        var listResponse = await _client.GetAsync($"/api/projects/{ProjectId}/pi-sessions?taskId={_task.Id}");
        listResponse.EnsureSuccessStatusCode();
        using var listJson = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        var listed = Assert.Single(listJson.RootElement.EnumerateArray());
        Assert.Equal("session-a", listed.GetProperty("session_id").GetString());
        Assert.Equal("running", listed.GetProperty("state").GetString());
        Assert.True(listed.TryGetProperty("last_activity_at", out _));

        var statusResponse = await _client.GetAsync($"/api/projects/{ProjectId}/pi-sessions/session-a");
        statusResponse.EnsureSuccessStatusCode();
        using var statusJson = JsonDocument.Parse(await statusResponse.Content.ReadAsStringAsync());
        Assert.Equal("session-a", statusJson.RootElement.GetProperty("session").GetProperty("session_id").GetString());

        var attachResponse = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/pi-sessions/session-a/attach", new
        {
            requested_by = "hermes",
            mode = "external_attach_info",
        });
        attachResponse.EnsureSuccessStatusCode();
        using var attachJson = JsonDocument.Parse(await attachResponse.Content.ReadAsStringAsync());
        Assert.Equal("tmux", attachJson.RootElement.GetProperty("backend").GetString());
        Assert.Contains("attach-session", attachJson.RootElement.GetProperty("command_args").EnumerateArray().Select(v => v.GetString()));
    }

    [Fact]
    public async Task StatusRefreshCapturesBoundedOutputAndSurfacesAttention()
    {
        var launch = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/pi-sessions", new
        {
            session_id = "session-attention",
            task_id = _task.Id,
            run_id = "run-attention",
            requested_by = "hermes",
            callback_ports = new[] { new { host_port = 21458, container_port = 1455 } },
        });
        launch.EnsureSuccessStatusCode();

        var now = DateTime.UtcNow;
        _factory.FakeHost.SetStatus("session-attention", new PiSessionHostStatus
        {
            State = PiSessionStates.Running,
            LastActivityAt = now,
            OutputTail = "working\nDo you want to continue? [y/N]",
            OutputTailCapturedAt = now.AddSeconds(1),
            OutputTailTruncated = true,
        });

        var detailResponse = await _client.GetAsync($"/api/projects/{ProjectId}/pi-sessions/session-attention");
        detailResponse.EnsureSuccessStatusCode();
        using var detailJson = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync());
        var session = detailJson.RootElement.GetProperty("session");
        Assert.Equal("run-attention", session.GetProperty("run_id").GetString());
        Assert.Contains("Do you want to continue", session.GetProperty("output_tail").GetString());
        Assert.True(session.GetProperty("output_tail_truncated").GetBoolean());
        Assert.Equal("user_input_needed", session.GetProperty("attention_state").GetString());
        Assert.True(session.GetProperty("needs_user_input").GetBoolean());
        Assert.True(session.TryGetProperty("attention_since_at", out _));
        Assert.True(session.TryGetProperty("last_activity_at", out _));

        var listResponse = await _client.GetAsync($"/api/projects/{ProjectId}/pi-sessions?taskId={_task.Id}");
        listResponse.EnsureSuccessStatusCode();
        using var listJson = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        var listed = listJson.RootElement.EnumerateArray().Single(s => s.GetProperty("session_id").GetString() == "session-attention");
        Assert.Equal("user_input_needed", listed.GetProperty("attention_state").GetString());
        Assert.True(listed.GetProperty("needs_user_input").GetBoolean());

        using var scope = _factory.Services.CreateScope();
        var stream = scope.ServiceProvider.GetRequiredService<IAgentStreamRepository>();
        var entries = await stream.ListAsync(new AgentStreamListOptions
        {
            ProjectId = ProjectId,
            TaskId = _task.Id,
            StreamKind = AgentStreamKind.Ops,
            IncludeDebug = true,
            Limit = 20,
        });
        var attentionEntry = Assert.Single(entries, e => e.EventType == "pi_session_attention_needed");
        Assert.Equal(AgentStreamDeliveryMode.Notify, attentionEntry.DeliveryMode);
        Assert.Contains(entries, e => e.EventType == "pi_session_output_tail_updated");
    }

    [Fact]
    public async Task ListCanFilterByAttentionStateAndNeedsUserInput()
    {
        var attentionLaunch = await LaunchSessionAsync("session-filter-attention");
        attentionLaunch.EnsureSuccessStatusCode();
        var passiveLaunch = await LaunchSessionAsync("session-filter-passive");
        passiveLaunch.EnsureSuccessStatusCode();

        var now = DateTime.UtcNow;
        _factory.FakeHost.SetStatus("session-filter-attention", new PiSessionHostStatus
        {
            State = PiSessionStates.Running,
            LastActivityAt = now,
            OutputTail = "approval required before proceeding",
            OutputTailCapturedAt = now,
        });
        _factory.FakeHost.SetStatus("session-filter-passive", new PiSessionHostStatus
        {
            State = PiSessionStates.Running,
            LastActivityAt = now.AddSeconds(-1),
            OutputTail = "still working",
            OutputTailCapturedAt = now,
        });

        var needsInput = await _client.GetAsync($"/api/projects/{ProjectId}/pi-sessions?taskId={_task.Id}&needsUserInput=true");
        needsInput.EnsureSuccessStatusCode();
        using var needsInputJson = JsonDocument.Parse(await needsInput.Content.ReadAsStringAsync());
        var needsInputSession = Assert.Single(needsInputJson.RootElement.EnumerateArray());
        Assert.Equal("session-filter-attention", needsInputSession.GetProperty("session_id").GetString());
        Assert.True(needsInputSession.GetProperty("needs_user_input").GetBoolean());

        var attentionState = await _client.GetAsync($"/api/projects/{ProjectId}/pi-sessions?taskId={_task.Id}&attentionState=waiting_for_direction");
        attentionState.EnsureSuccessStatusCode();
        using var attentionStateJson = JsonDocument.Parse(await attentionState.Content.ReadAsStringAsync());
        var attentionStateSession = Assert.Single(attentionStateJson.RootElement.EnumerateArray());
        Assert.Equal("session-filter-attention", attentionStateSession.GetProperty("session_id").GetString());

        var noInput = await _client.GetAsync($"/api/projects/{ProjectId}/pi-sessions?taskId={_task.Id}&needsUserInput=false");
        noInput.EnsureSuccessStatusCode();
        using var noInputJson = JsonDocument.Parse(await noInput.Content.ReadAsStringAsync());
        var noInputSessions = noInputJson.RootElement.EnumerateArray().Select(s => s.GetProperty("session_id").GetString()).ToList();
        Assert.Contains("session-filter-passive", noInputSessions);
        Assert.DoesNotContain("session-filter-attention", noInputSessions);
    }

    [Fact]
    public async Task StatusRefreshClearsAttentionWhenSessionCompletesAndPostsEvent()
    {
        var launch = await LaunchSessionAsync("session-clear-attention");
        launch.EnsureSuccessStatusCode();

        var now = DateTime.UtcNow;
        _factory.FakeHost.SetStatus("session-clear-attention", new PiSessionHostStatus
        {
            State = PiSessionStates.Running,
            LastActivityAt = now,
            OutputTail = "Do you want to continue? [y/N]",
            OutputTailCapturedAt = now,
        });

        var promptResponse = await _client.GetAsync($"/api/projects/{ProjectId}/pi-sessions/session-clear-attention");
        promptResponse.EnsureSuccessStatusCode();
        using (var promptJson = JsonDocument.Parse(await promptResponse.Content.ReadAsStringAsync()))
        {
            var prompted = promptJson.RootElement.GetProperty("session");
            Assert.Equal("user_input_needed", prompted.GetProperty("attention_state").GetString());
            Assert.True(prompted.GetProperty("needs_user_input").GetBoolean());
        }

        _factory.FakeHost.SetStatus("session-clear-attention", new PiSessionHostStatus
        {
            State = PiSessionStates.Completed,
            LastActivityAt = now.AddSeconds(10),
            OutputTail = "completed",
            OutputTailCapturedAt = now.AddSeconds(10),
        });

        var completedResponse = await _client.GetAsync($"/api/projects/{ProjectId}/pi-sessions/session-clear-attention");
        completedResponse.EnsureSuccessStatusCode();
        using var completedJson = JsonDocument.Parse(await completedResponse.Content.ReadAsStringAsync());
        var completed = completedJson.RootElement.GetProperty("session");
        Assert.Equal("completed", completed.GetProperty("state").GetString());
        Assert.True(!completed.TryGetProperty("attention_state", out var clearedAttentionState) || clearedAttentionState.ValueKind == JsonValueKind.Null);
        Assert.True(!completed.TryGetProperty("attention_reason", out var clearedAttentionReason) || clearedAttentionReason.ValueKind == JsonValueKind.Null);
        Assert.False(completed.GetProperty("needs_user_input").GetBoolean());

        using var scope = _factory.Services.CreateScope();
        var stream = scope.ServiceProvider.GetRequiredService<IAgentStreamRepository>();
        var entries = await stream.ListAsync(new AgentStreamListOptions
        {
            ProjectId = ProjectId,
            TaskId = _task.Id,
            StreamKind = AgentStreamKind.Ops,
            IncludeDebug = true,
            Limit = 20,
        });
        Assert.Contains(entries, e => e.EventType == "pi_session_attention_cleared");
    }

    [Fact]
    public async Task StaleActivityIsSurfacedAsStalledAttention()
    {
        var launch = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/pi-sessions", new
        {
            session_id = "session-stalled",
            task_id = _task.Id,
            requested_by = "hermes",
            callback_ports = new[] { new { host_port = 21459, container_port = 1455 } },
        });
        launch.EnsureSuccessStatusCode();

        _factory.FakeHost.SetStatus("session-stalled", new PiSessionHostStatus
        {
            State = PiSessionStates.Running,
            LastActivityAt = DateTime.UtcNow.AddMinutes(-45),
            OutputTail = "still compiling",
            OutputTailCapturedAt = DateTime.UtcNow,
        });

        var detailResponse = await _client.GetAsync($"/api/projects/{ProjectId}/pi-sessions/session-stalled");
        detailResponse.EnsureSuccessStatusCode();
        using var detailJson = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync());
        var session = detailJson.RootElement.GetProperty("session");
        Assert.Equal("stalled", session.GetProperty("attention_state").GetString());
        Assert.False(session.GetProperty("needs_user_input").GetBoolean());
        Assert.Contains("No host-reported activity", session.GetProperty("attention_reason").GetString());
    }

    [Fact]
    public async Task TerminateAndCleanupAreExplicitAndAudited()
    {
        var launch = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/pi-sessions", new
        {
            session_id = "session-b",
            task_id = _task.Id,
            requested_by = "hermes",
            callback_ports = new[] { new { host_port = 21456, container_port = 1455 } },
        });
        launch.EnsureSuccessStatusCode();

        var terminate = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/pi-sessions/session-b/terminate", new
        {
            requested_by = "hermes",
            reason = "done",
        });
        terminate.EnsureSuccessStatusCode();
        using var terminatedJson = JsonDocument.Parse(await terminate.Content.ReadAsStringAsync());
        var terminated = terminatedJson.RootElement.GetProperty("session");
        Assert.Equal("completed", terminated.GetProperty("state").GetString());
        Assert.Equal("hermes", terminated.GetProperty("termination_requested_by").GetString());
        Assert.Equal("done", terminated.GetProperty("termination_reason").GetString());
        Assert.True(terminated.TryGetProperty("ended_at", out _));

        var cleanup = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/pi-sessions/session-b/cleanup", new
        {
            requested_by = "hermes",
            reason = "remove compose leftovers",
        });
        cleanup.EnsureSuccessStatusCode();
        using var cleanupJson = JsonDocument.Parse(await cleanup.Content.ReadAsStringAsync());
        var cleaned = cleanupJson.RootElement.GetProperty("session");
        Assert.Equal("hermes", cleaned.GetProperty("cleanup_requested_by").GetString());
        Assert.Equal("remove compose leftovers", cleaned.GetProperty("cleanup_reason").GetString());
        Assert.True(cleaned.TryGetProperty("cleanup_completed_at", out _));

        using var scope = _factory.Services.CreateScope();
        var stream = scope.ServiceProvider.GetRequiredService<IAgentStreamRepository>();
        var entries = await stream.ListAsync(new AgentStreamListOptions
        {
            ProjectId = ProjectId,
            TaskId = _task.Id,
            StreamKind = AgentStreamKind.Ops,
            IncludeDebug = true,
            Limit = 20,
        });
        Assert.Contains(entries, e => e.EventType == "pi_session_terminate_requested");
        Assert.Contains(entries, e => e.EventType == "pi_session_cleanup_completed");
        foreach (var piSessionEntry in entries.Where(e => e.EventType.StartsWith("pi_session_", StringComparison.Ordinal)))
        {
            Assert.NotNull(piSessionEntry.DedupKey);
            Assert.StartsWith("pi-session-event:", piSessionEntry.DedupKey);
        }
    }

    [Fact]
    public async Task TerminateAlreadyCompletedSessionReturnsConflict()
    {
        var launch = await LaunchSessionAsync("session-completed");
        launch.EnsureSuccessStatusCode();

        var terminate = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/pi-sessions/session-completed/terminate", new
        {
            requested_by = "hermes",
            reason = "done",
        });
        terminate.EnsureSuccessStatusCode();

        var secondTerminate = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/pi-sessions/session-completed/terminate", new
        {
            requested_by = "hermes",
            reason = "retry",
        });

        Assert.Equal(HttpStatusCode.Conflict, secondTerminate.StatusCode);
        using var json = JsonDocument.Parse(await secondTerminate.Content.ReadAsStringAsync());
        Assert.Contains("already completed", json.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task CleanupActiveSessionReturnsConflict()
    {
        var launch = await LaunchSessionAsync("session-active-cleanup");
        launch.EnsureSuccessStatusCode();

        var cleanup = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/pi-sessions/session-active-cleanup/cleanup", new
        {
            requested_by = "hermes",
            reason = "too soon",
        });

        Assert.Equal(HttpStatusCode.Conflict, cleanup.StatusCode);
        using var json = JsonDocument.Parse(await cleanup.Content.ReadAsStringAsync());
        Assert.Contains("terminate it before cleanup", json.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task MissingSessionEndpointsReturnNotFound()
    {
        var detail = await _client.GetAsync($"/api/projects/{ProjectId}/pi-sessions/missing-session");
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);

        var attach = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/pi-sessions/missing-session/attach", new
        {
            requested_by = "hermes",
        });
        Assert.Equal(HttpStatusCode.NotFound, attach.StatusCode);

        var terminate = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/pi-sessions/missing-session/terminate", new
        {
            requested_by = "hermes",
            reason = "not there",
        });
        Assert.Equal(HttpStatusCode.NotFound, terminate.StatusCode);

        var cleanup = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/pi-sessions/missing-session/cleanup", new
        {
            requested_by = "hermes",
            reason = "not there",
        });
        Assert.Equal(HttpStatusCode.NotFound, cleanup.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("session with spaces")]
    [InlineData("session\twith-tab")]
    public async Task LaunchRejectsExplicitInvalidSessionId(string sessionId)
    {
        var response = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/pi-sessions", new
        {
            session_id = sessionId,
            task_id = _task.Id,
            callback_ports = new[] { new { host_port = 21460, container_port = 1455 } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains("session_id must", json.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task LaunchRejectsMissingTaskLink()
    {
        var response = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/pi-sessions", new
        {
            session_id = "session-c",
            callback_ports = new[] { new { host_port = 21457, container_port = 1455 } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains("task_id is required", json.RootElement.GetProperty("error").GetString());
    }

    private Task<HttpResponseMessage> LaunchSessionAsync(string sessionId) =>
        _client.PostAsJsonAsync($"/api/projects/{ProjectId}/pi-sessions", new
        {
            session_id = sessionId,
            task_id = _task.Id,
            requested_by = "hermes",
            callback_ports = new[] { new { host_port = 21461, container_port = 1455 } },
        });

    private sealed class PiSessionAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"den-mcp-pi-session-api-{Guid.NewGuid()}.db");
        public FakePiSessionHost FakeHost { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DenMcp:DatabasePath"] = _dbPath,
                    ["DenMcp:Llm:Endpoint"] = "",
                    ["DenMcp:Llm:Model"] = "test-model",
                    ["DenMcp:PiSessionHost:ComposeFile"] = "/opt/pi-docker/compose.yaml",
                    ["DenMcp:PiSessionHost:DevDir"] = "/srv/dev",
                    ["DenMcp:PiSessionHost:PiStateRootDir"] = "/srv/pi-state",
                    ["DenMcp:PiSessionHost:Image"] = "pi-sandbox:test",
                    ["DenMcp:PiSessionHost:PiVersion"] = "0.71.0",
                    ["DenMcp:PiSessionHost:NodeVersion"] = "22",
                    ["DenMcp:PiSessionHost:GitConfigPath"] = "/home/patch/.gitconfig",
                    ["DenMcp:PiSessionHost:SshDir"] = "/home/patch/.ssh",
                    ["DenMcp:PiSessionHost:GhConfigDir"] = "/home/patch/.config/gh",
                    ["DenMcp:PiSessionHost:HostId"] = "host-test",
                    ["DenMcp:PiSessionHost:DockerHost"] = "unix:///run/den-mcp/docker-rt/docker.sock",
                });
            });

            builder.ConfigureTestServices(services =>
            {
                var initializer = new DatabaseInitializer(_dbPath, NullLogger<DatabaseInitializer>.Instance);
                initializer.InitializeAsync().GetAwaiter().GetResult();

                services.RemoveAll<DbConnectionFactory>();
                services.AddSingleton(new DbConnectionFactory(initializer.ConnectionString));

                services.RemoveAll<ILlmClient>();
                services.AddSingleton<ILlmClient>(new NoOpLlmClient());

                services.RemoveAll<PiDockerLaunchProfileOptions>();
                services.AddSingleton(new PiDockerLaunchProfileOptions
                {
                    ComposeFile = "/opt/pi-docker/compose.yaml",
                    DevDir = "/srv/dev",
                    PiStateRootDir = "/srv/pi-state",
                    Image = "pi-sandbox:test",
                    PiVersion = "0.71.0",
                    NodeVersion = "22",
                    GitConfigPath = "/home/patch/.gitconfig",
                    SshDir = "/home/patch/.ssh",
                    GhConfigDir = "/home/patch/.config/gh",
                    HostId = "host-test",
                    DockerHost = "unix:///run/den-mcp/docker-rt/docker.sock",
                });

                services.RemoveAll<IPiSessionHost>();
                services.AddSingleton<IPiSessionHost>(FakeHost);
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && File.Exists(_dbPath))
                File.Delete(_dbPath);
        }
    }

    private sealed class FakePiSessionHost : IPiSessionHost
    {
        private readonly Dictionary<string, PiSessionHostStatus> _statuses = new(StringComparer.Ordinal);
        public List<PiSessionLaunchPlan> Launches { get; } = [];
        public string HostId => "host-test";

        public Task<PiSessionHostLaunchResult> LaunchAsync(PiSessionLaunchPlan plan, CancellationToken cancellationToken = default)
        {
            Launches.Add(plan);
            var now = DateTime.UtcNow;
            _statuses[plan.Record.SessionId] = new PiSessionHostStatus
            {
                State = PiSessionStates.Running,
                LastActivityAt = now.AddSeconds(1),
                ContainerName = plan.Record.ContainerName,
            };
            return Task.FromResult(new PiSessionHostLaunchResult
            {
                State = PiSessionStates.Running,
                StartedAt = now,
                LastActivityAt = now,
                ContainerName = plan.Record.ContainerName,
            });
        }

        public void SetStatus(string sessionId, PiSessionHostStatus status) => _statuses[sessionId] = status;

        public Task<PiSessionHostStatus> GetStatusAsync(PiSessionRecord session, CancellationToken cancellationToken = default) =>
            Task.FromResult(_statuses.GetValueOrDefault(session.SessionId) ?? new PiSessionHostStatus { State = PiSessionStates.Stale });

        public Task<PiSessionHostControlResult> TerminateAsync(PiSessionRecord session, CancellationToken cancellationToken = default)
        {
            _statuses[session.SessionId] = new PiSessionHostStatus { State = PiSessionStates.Completed };
            return Task.FromResult(new PiSessionHostControlResult
            {
                Succeeded = true,
                State = PiSessionStates.Completed,
                EndedAt = DateTime.UtcNow,
                StateReason = "terminated by fake host",
            });
        }

        public Task<PiSessionHostControlResult> CleanupAsync(PiSessionRecord session, PiDockerLaunchProfile? profile, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PiSessionHostControlResult
            {
                Succeeded = true,
                State = session.State,
                StateReason = "cleanup by fake host",
            });
    }

    private sealed class NoOpLlmClient : ILlmClient
    {
        public Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct = default) => Task.FromResult("{}");
    }
}
