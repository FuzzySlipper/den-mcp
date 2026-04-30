using System.Text.Json;
using Den.Bridge.Abstractions;
using Den.Bridge.Protocol;
using DenMcp.Desktop.Sidecar;
using Microsoft.Extensions.DependencyInjection;

namespace DenMcp.Desktop.Sidecar.Tests;

public class AppAgentBridgeTests
{
    [Fact]
    public void ToolRegistry_ExposesAllowListedReadDraftActionToolsOnly()
    {
        var registry = new AppAgentToolRegistry();
        var tools = registry.ListTools();

        Assert.Equal(new[]
        {
            "get_context",
            "list_sessions",
            "read_activity",
            "read_terminal",
            "get_git_snapshot",
            "list_den_messages",
            "list_console_commands",
            "summarize_output",
            "draft_den_message",
            "draft_task_update",
            "run_command",
            "send_compiled_response",
            "cancel_request",
            "stop_agent_run",
        }, tools.Select(tool => tool.Name).ToArray());
        Assert.Contains(tools, tool => tool.Name == "run_command" && tool.Category == "action" && tool.Capabilities.Contains("console.run"));
        var stopTool = Assert.Single(tools, tool => tool.Name == "stop_agent_run");
        Assert.False(stopTool.Enabled);
        Assert.Equal(AppAgentToolRegistry.StopAgentRunDisabledReason, stopTool.DisabledReason);
        var disabled = Assert.Throws<BridgeHandlerException>(() => registry.GetRequired("stop_agent_run"));
        Assert.Equal("app_agent.tool.disabled", disabled.Code);
        Assert.Equal("unsupported_capability", disabled.Category);
        Assert.DoesNotContain(tools, tool => tool.Name.Contains("shell", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(tools, tool => tool.Name.Contains("dispatch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BuildContext_ReturnsCuratedPacketWithExplicitActivityOnly()
    {
        using var provider = DesktopSidecarBridge.CreateServiceProvider(DesktopSidecarFixtures.CreateFixtureOptions());
        var registry = provider.GetRequiredService<OperatorSessionRegistry>();
        registry.Register(new OperatorSession
        {
            SessionId = "session-1",
            ProjectId = "den-mcp",
            TaskId = 1023,
            Title = "Coder",
            DisplayName = "Coder session",
            Kind = OperatorSessionKind.Agent,
            Backend = OperatorSessionBackend.PiArtifact,
            Status = OperatorSessionStatus.Running,
            Capabilities = OperatorSessionCapabilities.ObserveOnly("fixture", canReadActivity: true),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            SourceInstanceId = "fixture-source",
            RecentActivity =
            [
                new OperatorSessionActivityItem
                {
                    Kind = "tool",
                    Tool = "read",
                    Summary = "Read bounded file summary",
                    Timestamp = "2026-04-29T12:34:56.000Z",
                },
            ],
        });

        var service = provider.GetRequiredService<AppAgentService>();
        var context = await service.BuildContextAsync(new AppAgentBuildContextRequest
        {
            Selection = new AppAgentSelection { ProjectId = "den-mcp", TaskId = 1023, SessionId = "session-1" },
            AgentRunId = "run-fixture",
            TraceId = "trace-fixture",
            TerminalExcerpts = [new AppAgentTerminalExcerptRequest { SessionId = "session-1", Limit = 10 }],
        }, CancellationToken.None);

        Assert.Equal(1, context.ContextVersion);
        Assert.Equal("den-mcp", context.Selection.ProjectId);
        Assert.Contains(context.Authority.AllowedTools, tool => tool.Name == "get_context");
        Assert.DoesNotContain(context.Authority.AllowedTools, tool => tool.Name == "stop_agent_run");
        var disabledStopTool = Assert.Single(context.Authority.DisabledTools, tool => tool.Name == "stop_agent_run");
        Assert.Equal(AppAgentToolRegistry.StopAgentRunDisabledReason, disabledStopTool.Reason);
        Assert.True(context.Authority.CancelAvailable);
        Assert.False(context.Authority.StopAvailable);
        Assert.Contains(context.CommandSummaries, command => command.Name == "help");
        Assert.Single(context.SessionSummaries);
        Assert.Single(context.TerminalExcerpts);
        Assert.False(context.TerminalExcerpts[0].RawTerminalBytesPersisted);
        Assert.Equal("Read bounded file summary", context.TerminalExcerpts[0].Items[0].Summary);
        Assert.Equal("run-fixture", context.Audit.AgentRunId);
    }

    [Fact]
    public async Task InvokeTool_RunsOnlyStructuredConsoleCommandsAndAuditsLocally()
    {
        using var provider = DesktopSidecarBridge.CreateServiceProvider(DesktopSidecarFixtures.CreateFixtureOptions());
        var service = provider.GetRequiredService<AppAgentService>();
        var audit = provider.GetRequiredService<AppAgentAuditService>();

        var response = await service.InvokeToolAsync(
            "req_tool_001",
            new AppAgentInvokeToolRequest
            {
                ToolName = "run_command",
                AgentRunId = "run-command-fixture",
                Input = JsonSerializer.Deserialize<JsonElement>("{\"command\":\"help\"}"),
            },
            CancellationToken.None);

        Assert.Equal("run_command", response.ToolName);
        Assert.Equal("completed", response.Status);
        Assert.Equal("success", response.Result.GetProperty("status").GetString());
        Assert.Contains(response.Result.GetProperty("lines").EnumerateArray(), line =>
            line.GetProperty("message").GetString()!.Contains("Available commands", StringComparison.Ordinal));
        Assert.Contains(audit.LocalEvents, entry => entry.EventType == "app_agent.tool_completed");
        Assert.All(audit.LocalEvents, entry => Assert.DoesNotContain("raw terminal", entry.Payload ?? string.Empty, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BridgeRouter_RegistersTypedAppAgentCommandsAndRejectsUnknownTool()
    {
        using var provider = DesktopSidecarBridge.CreateServiceProvider(DesktopSidecarFixtures.CreateFixtureOptions());
        var router = provider.GetRequiredService<IBridgeCommandRouter>();

        var toolsResponse = await router.DispatchAsync(new BridgeRequestFrame
        {
            SchemaVersion = DesktopSidecarProtocol.SchemaVersion,
            RequestId = "req_tools",
            Command = DesktopSidecarProtocol.AppAgentListToolsCommand,
            Payload = BridgeJson.EmptyObject(),
            SentAt = DateTimeOffset.Parse("2026-04-29T12:34:56.000Z"),
        });
        Assert.Null(toolsResponse.Error);
        Assert.Contains(toolsResponse.Result!.Value.GetProperty("tools").EnumerateArray(), tool =>
            tool.GetProperty("name").GetString() == "get_context");
        Assert.Contains(toolsResponse.Result!.Value.GetProperty("tools").EnumerateArray(), tool =>
            tool.GetProperty("name").GetString() == "stop_agent_run"
            && !tool.GetProperty("enabled").GetBoolean()
            && tool.GetProperty("disabled_reason").GetString() == AppAgentToolRegistry.StopAgentRunDisabledReason);

        var stopResponse = await router.DispatchAsync(new BridgeRequestFrame
        {
            SchemaVersion = DesktopSidecarProtocol.SchemaVersion,
            RequestId = "req_stop_tool",
            Command = DesktopSidecarProtocol.AppAgentInvokeToolCommand,
            Payload = JsonSerializer.Deserialize<JsonElement>("{\"tool_name\":\"stop_agent_run\",\"input\":{}}"),
            SentAt = DateTimeOffset.Parse("2026-04-29T12:34:56.000Z"),
        });
        Assert.NotNull(stopResponse.Error);
        Assert.Equal("app_agent.tool.disabled", stopResponse.Error!.Code);
        Assert.Equal("unsupported_capability", stopResponse.Error.Category);
        Assert.Equal(AppAgentToolRegistry.StopAgentRunDisabledReason, stopResponse.Error.Message);

        var invalidResponse = await router.DispatchAsync(new BridgeRequestFrame
        {
            SchemaVersion = DesktopSidecarProtocol.SchemaVersion,
            RequestId = "req_invalid_tool",
            Command = DesktopSidecarProtocol.AppAgentInvokeToolCommand,
            Payload = JsonSerializer.Deserialize<JsonElement>("{\"tool_name\":\"shell\",\"input\":{}}"),
            SentAt = DateTimeOffset.Parse("2026-04-29T12:34:56.000Z"),
        });
        Assert.NotNull(invalidResponse.Error);
        Assert.Equal("app_agent.tool.not_found", invalidResponse.Error!.Code);
    }
}
