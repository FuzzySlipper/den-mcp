using System.Text.Json;
using DenMcp.Core.Models;
using DenMcp.Core.Services;
using DenMcp.Server.Tools;

namespace DenMcp.Server.Tests;

public class WorkerObservabilityToolsTests
{
    [Fact]
    public void PiDockerLaunchProfileRenderer_IncludesBoundedWorkerStartupEnvironment()
    {
        var renderer = new PiDockerLaunchProfileRenderer(new PiDockerLaunchProfileOptions
        {
            ComposeFile = "/tmp/den-mcp/compose.yaml",
            DevDir = "/tmp/den-mcp/dev",
            PiStateRootDir = "/tmp/den-mcp/pi-state",
            CredentialFallbackRootDir = "/tmp/den-mcp/creds",
        });

        var profile = renderer.Render(new PiDockerLaunchRenderRequest
        {
            ProjectId = "proj",
            SessionId = "session-1",
            TaskId = 1245,
            WorkerRole = "validator",
            WorkerRunId = "run-1",
            PromptPacketMessageId = 5549,
            StateFileRef = "den-state://worker/run-1/startup.json",
            StartupPrompt = "Read Den packet #5549 before work.",
            TimeoutSeconds = 600,
            CallbackPorts = [new PiDockerCallbackPort { HostPort = 21455, ContainerPort = 1455 }]
        });

        Assert.Equal("5549", profile.Environment["DEN_WORKER_PROMPT_PACKET_MESSAGE_ID"]);
        Assert.Equal("den-state://worker/run-1/startup.json", profile.Environment["DEN_WORKER_STATE_FILE_REF"]);
        Assert.Equal("Read Den packet #5549 before work.", profile.Environment["DEN_WORKER_STARTUP_PROMPT"]);
        Assert.Equal("validator", profile.Environment["DEN_WORKER_ROLE"]);
        Assert.Equal("run-1", profile.Environment["DEN_WORKER_RUN_ID"]);
        Assert.DoesNotContain("Read Den packet #5549", profile.DockerComposeRunArgs);
        Assert.Equal(5549, profile.PromptPacketMessageId);
        Assert.Equal(600, profile.TimeoutSeconds);
    }

    [Fact]
    public async Task LaunchPiWorker_ProjectsStartupContractIntoWorkerRun()
    {
        var service = new CapturingPiSessionService();
        var json = await WorkerTools.LaunchPiWorker(
            service,
            project_id: "proj",
            requested_by: "runner",
            role: "validator",
            task_id: 1245,
            prompt_packet_message_id: 5549,
            run_id: "run-1",
            session_id: "session-1",
            timeout_seconds: 600,
            callback_ports: "[{\"host_port\":21455,\"container_port\":1455}]",
            verbose: true);

        using var doc = JsonDocument.Parse(json);
        var worker = doc.RootElement.GetProperty("worker_run");
        Assert.Equal("5549", worker.GetProperty("startup_contract").GetProperty("prompt_packet_message_id").GetRawText());
        Assert.True(worker.GetProperty("startup_contract").GetProperty("delivered_via_environment").GetBoolean());
        Assert.Contains("DEN_WORKER_STARTUP_PROMPT", worker.GetProperty("startup_contract").GetProperty("environment_keys").EnumerateArray().Select(e => e.GetString()));
        Assert.NotNull(service.CapturedRequest);
        Assert.Contains("prompt_packet_message_id: `5549`", service.CapturedRequest!.StartupPrompt);
    }

    [Fact]
    public async Task CleanupWorkerRun_WhenAlreadyCleaned_IsIdempotentNoop()
    {
        var service = new CapturingPiSessionService
        {
            Detail = CapturingPiSessionService.CreateDetail(state: PiSessionStates.Completed, cleanupCompleted: DateTime.UtcNow)
        };

        var json = await WorkerTools.CleanupWorkerRun(service, "proj", "run-1", "runner", verbose: true);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("noop", doc.RootElement.GetProperty("cleanup").GetProperty("status").GetString());
        Assert.Equal("cleaned_up", doc.RootElement.GetProperty("cleanup").GetProperty("state").GetString());
        Assert.Equal(0, service.CleanupCalls);
    }

    private sealed class CapturingPiSessionService : IPiSessionService
    {
        public PiSessionLaunchRequest? CapturedRequest { get; private set; }
        public int CleanupCalls { get; private set; }
        public PiSessionDetail Detail { get; set; } = CreateDetail();

        public Task<PiSessionDetail> LaunchAsync(string projectId, PiSessionLaunchRequest request, CancellationToken cancellationToken = default)
        {
            CapturedRequest = request;
            Detail = CreateDetail(projectId, request.SessionId ?? "session-1", request.RunId ?? "run-1", request.TaskId, PiSessionStates.Running, request.PromptPacketMessageId, request.StateFileRef, request.StartupPrompt, request.TimeoutSeconds);
            return Task.FromResult(Detail);
        }

        public Task<List<PiSessionSummary>> ListAsync(PiSessionListOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult(new List<PiSessionSummary> { Detail.Session });

        public Task<PiSessionDetail?> GetAsync(string projectId, string sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult<PiSessionDetail?>(Detail.Session.ProjectId == projectId && (Detail.Session.SessionId == sessionId || Detail.Session.RunId == sessionId) ? Detail : null);

        public Task<PiSessionDetail?> TerminateAsync(string projectId, string sessionId, PiSessionControlRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<PiSessionDetail?>(Detail);

        public Task<PiSessionDetail?> CleanupAsync(string projectId, string sessionId, PiSessionControlRequest request, CancellationToken cancellationToken = default)
        {
            CleanupCalls++;
            Detail = CreateDetail(projectId, sessionId, Detail.Session.RunId ?? "run-1", Detail.Session.TaskId, PiSessionStates.Completed, cleanupCompleted: DateTime.UtcNow);
            return Task.FromResult<PiSessionDetail?>(Detail);
        }

        public Task<PiSessionAttachInfo?> GetAttachInfoAsync(string projectId, string sessionId, PiSessionAttachRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<PiSessionAttachInfo?>(null);

        public static PiSessionDetail CreateDetail(
            string projectId = "proj",
            string sessionId = "session-1",
            string runId = "run-1",
            int? taskId = 1245,
            string state = PiSessionStates.Completed,
            int? promptPacketMessageId = null,
            string? stateFileRef = null,
            string? startupPrompt = null,
            int? timeoutSeconds = null,
            DateTime? cleanupCompleted = null)
        {
            var env = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["DEN_WORKER_PROJECT_ID"] = projectId,
                ["DEN_WORKER_SESSION_ID"] = sessionId,
            };
            if (promptPacketMessageId is not null)
                env["DEN_WORKER_PROMPT_PACKET_MESSAGE_ID"] = promptPacketMessageId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (startupPrompt is not null)
                env["DEN_WORKER_STARTUP_PROMPT"] = startupPrompt;

            return new PiSessionDetail
            {
                Session = new PiSessionSummary
                {
                    ProjectId = projectId,
                    SessionId = sessionId,
                    RunId = runId,
                    TaskId = taskId,
                    ToolProfile = "validator",
                    HostId = "test-host",
                    TmuxSessionName = "den-proj-session-1",
                    ContainerName = "den-proj-session-1-pi",
                    State = state,
                    StateReason = state == PiSessionStates.Completed ? "worker completion packet #1: completed" : null,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    CleanupCompletedAt = cleanupCompleted,
                },
                LaunchProfile = new PiDockerLaunchProfile
                {
                    ProfileId = "profile-1",
                    ProjectId = projectId,
                    SessionId = sessionId,
                    ComposeProjectName = "den-proj-session-1",
                    ComposeFile = "/tmp/compose.yaml",
                    Service = "pi",
                    DevDir = "/tmp/dev",
                    PiStateDir = "/tmp/pi-state",
                    Image = "pi-sandbox:latest",
                    PiVersion = "0.71.0",
                    NodeVersion = "22",
                    Environment = env,
                    PromptPacketMessageId = promptPacketMessageId,
                    StateFileRef = stateFileRef,
                    StartupPrompt = startupPrompt,
                    TimeoutSeconds = timeoutSeconds,
                }
            };
        }
    }
}
