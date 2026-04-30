namespace DenMcp.Desktop.Sidecar;

/// <summary>
/// Console command runner that implements safe built-in actions through existing
/// runtime services (OperatorRuntimeService, OperatorSessionRegistry, GitSnapshotBuilder).
/// No arbitrary shell execution is exposed through this runner.
/// </summary>
public sealed class ConsoleCommandRunner : IConsoleCommandRunner
{
    private static readonly IReadOnlyList<ConsoleCommandDefinition> BuiltInCommands = new[]
    {
        new ConsoleCommandDefinition
        {
            Name = "help",
            DisplayName = "Help",
            Description = "List all available console commands.",
            NeedsTarget = false,
        },
        new ConsoleCommandDefinition
        {
            Name = "refresh",
            DisplayName = "Refresh",
            Description = "Trigger a Den operator refresh cycle (projects, workspaces, snapshots).",
            NeedsTarget = false,
        },
        new ConsoleCommandDefinition
        {
            Name = "inspect-connection",
            DisplayName = "Inspect Connection",
            Description = "Show current Den server connection status and diagnostics.",
            NeedsTarget = false,
        },
        new ConsoleCommandDefinition
        {
            Name = "publish-snapshot",
            DisplayName = "Publish Snapshot",
            Description = "Publish the latest local git/session snapshot to Den now.",
            NeedsTarget = false,
        },
        new ConsoleCommandDefinition
        {
            Name = "list-sessions",
            DisplayName = "List Sessions",
            Description = "List observed operator sessions (Pi artifact sessions).",
            NeedsTarget = false,
        },
        new ConsoleCommandDefinition
        {
            Name = "git-status",
            DisplayName = "Git Status",
            Description = "Show git status summary for the selected workspace or first active project.",
            NeedsTarget = true,
        },
        new ConsoleCommandDefinition
        {
            Name = "diagnostics",
            DisplayName = "Diagnostics",
            Description = "Show recent runtime diagnostics entries.",
            NeedsTarget = false,
        },
        new ConsoleCommandDefinition
        {
            Name = "clear-diagnostics",
            DisplayName = "Clear Diagnostics",
            Description = "Clear the runtime diagnostics buffer.",
            NeedsTarget = false,
        },
    };

    private readonly OperatorRuntimeService _runtime;
    private readonly OperatorSessionRegistry _sessionRegistry;
    private readonly Func<DateTimeOffset> _now;

    public ConsoleCommandRunner(OperatorRuntimeService runtime, OperatorSessionRegistry sessionRegistry, Func<DateTimeOffset>? now = null)
    {
        _runtime = runtime;
        _sessionRegistry = sessionRegistry;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<ConsoleCommandDefinition> ListCommands()
    {
        return BuiltInCommands;
    }

    public async Task<ConsoleCommandRunResponse> RunCommandAsync(ConsoleCommandRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Command switch
        {
            "help" => RunHelp(),
            "refresh" => await RunRefreshAsync(cancellationToken).ConfigureAwait(false),
            "inspect-connection" => await RunInspectConnectionAsync(cancellationToken).ConfigureAwait(false),
            "publish-snapshot" => await RunRefreshAsync(cancellationToken).ConfigureAwait(false),
            "list-sessions" => RunListSessions(),
            "git-status" => await RunGitStatusAsync(request, cancellationToken).ConfigureAwait(false),
            "diagnostics" => await RunDiagnosticsAsync(cancellationToken).ConfigureAwait(false),
            "clear-diagnostics" => await RunClearDiagnosticsAsync(cancellationToken).ConfigureAwait(false),
            _ => UnknownCommand(request.Command),
        };
    }

    private ConsoleCommandRunResponse RunHelp()
    {
        var ts = NowString();
        var lines = new List<ConsoleCommandLine>
        {
            Line("ok", "console", $"Available commands ({BuiltInCommands.Count}):", ts),
        };

        foreach (var cmd in BuiltInCommands)
        {
            var targetHint = cmd.NeedsTarget ? " [needs target]" : "";
            lines.Add(Line("info", "console", $"  {cmd.Name,-20} {cmd.DisplayName,-15} {cmd.Description}{targetHint}", ts));
        }

        return Ok("help", lines);
    }

    private async Task<ConsoleCommandRunResponse> RunRefreshAsync(CancellationToken cancellationToken)
    {
        var ts = NowString();
        var lines = new List<ConsoleCommandLine>
        {
            Line("info", "console", "Triggering operator refresh cycle...", ts),
        };

        try
        {
            await _runtime.RefreshAsync(cancellationToken).ConfigureAwait(false);
            var status = await _runtime.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            lines.Add(Line("ok", "console", $"Refresh complete. {status.ProjectCount} projects, {status.WorkspaceCount} workspaces, {status.LocalSnapshotCount} snapshots.", ts));
            return Ok("refresh", lines);
        }
        catch (Exception ex)
        {
            lines.Add(Line("err", "console", $"Refresh failed: {ex.Message}", ts));
            return Error("refresh", ex.Message, lines);
        }
    }

    private async Task<ConsoleCommandRunResponse> RunInspectConnectionAsync(CancellationToken cancellationToken)
    {
        var status = await _runtime.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var ts = NowString();
        var conn = status.DenConnection;

        var lines = new List<ConsoleCommandLine>
        {
            Line("info", "console", "--- Den Connection Status ---", ts),
            Line("info", "den", $"State: {conn.State}", ts),
            Line("info", "den", $"URL:   {status.DenBaseUrl}", ts),
            Line("info", "den", $"Phase: {status.Phase}", ts),
        };

        if (!string.IsNullOrWhiteSpace(conn.Message))
            lines.Add(Line("info", "den", $"Message: {conn.Message}", ts));
        if (!string.IsNullOrWhiteSpace(conn.LastSuccessAt))
            lines.Add(Line("info", "den", $"Last success: {conn.LastSuccessAt}", ts));
        if (!string.IsNullOrWhiteSpace(conn.LastFailureAt))
            lines.Add(Line("warn", "den", $"Last failure: {conn.LastFailureAt}", ts));
        if (!string.IsNullOrWhiteSpace(conn.NextRetryAt))
            lines.Add(Line("info", "den", $"Next retry: {conn.NextRetryAt}", ts));

        lines.Add(Line("info", "console", "--- Observers ---", ts));
        foreach (var observer in status.ObserverStatuses)
        {
            var warnPart = observer.WarningCount > 0 ? $" ({observer.WarningCount} warnings)" : "";
            lines.Add(Line("info", observer.Kind, $"  {observer.Kind}: {observer.State}{warnPart}", ts));
        }

        return Ok("inspect-connection", lines);
    }

    private ConsoleCommandRunResponse RunListSessions()
    {
        var ts = NowString();
        var sessions = _sessionRegistry.List();
        var lines = new List<ConsoleCommandLine>
        {
            Line("info", "console", $"Operator sessions ({sessions.Count}):", ts),
        };

        foreach (var session in sessions)
        {
            lines.Add(Line("info", "session", $"  {session.SessionId} — {session.Kind ?? "unknown"} ({session.Status ?? "unknown"})", ts));
        }

        if (sessions.Count == 0)
        {
            lines.Add(Line("info", "session", "  (no sessions observed)", ts));
        }

        return Ok("list-sessions", lines);
    }

    private async Task<ConsoleCommandRunResponse> RunGitStatusAsync(ConsoleCommandRunRequest request, CancellationToken cancellationToken)
    {
        var ts = NowString();
        var snapshotList = await _runtime.ListLocalSnapshotsAsync(cancellationToken).ConfigureAwait(false);
        var snapshots = snapshotList.Snapshots;

        // Filter by optional target hints
        var filtered = snapshots;
        if (!string.IsNullOrWhiteSpace(request.ProjectId))
        {
            filtered = filtered.Where(s => s.Scope.ProjectId == request.ProjectId).ToList();
        }

        if (!string.IsNullOrWhiteSpace(request.WorkspaceId))
        {
            filtered = filtered.Where(s => s.Scope.WorkspaceId == request.WorkspaceId).ToList();
        }

        var lines = new List<ConsoleCommandLine>
        {
            Line("info", "console", $"Git status for {filtered.Count} workspace(s):", ts),
        };

        foreach (var snapshot in filtered)
        {
            var req = snapshot.Request;
            var branch = req.Branch ?? "(detached)";
            var dirty = req.DirtyCounts.Total > 0 ? $"±{req.DirtyCounts.Total}" : "clean";
            var head = req.HeadSha is { Length: > 8 } ? req.HeadSha[..8] : req.HeadSha ?? "—";
            lines.Add(Line("info", "git", $"  {snapshot.Scope.ProjectId}/{snapshot.Scope.RootPath}", ts));
            lines.Add(Line("info", "git", $"    branch: {branch}  head: {head}  dirty: {dirty}", ts));

            if (req.Warnings.Count > 0)
            {
                foreach (var warning in req.Warnings.Take(3))
                {
                    lines.Add(Line("warn", "git", $"    warning: {warning}", ts));
                }
            }
        }

        if (filtered.Count == 0)
        {
            lines.Add(Line("info", "git", "  (no matching workspaces)", ts));
        }

        return Ok("git-status", lines);
    }

    private async Task<ConsoleCommandRunResponse> RunDiagnosticsAsync(CancellationToken cancellationToken)
    {
        var status = await _runtime.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var ts = NowString();
        var diagnostics = status.Diagnostics;
        var lines = new List<ConsoleCommandLine>
        {
            Line("info", "console", $"Recent diagnostics ({diagnostics.Count} entries):", ts),
        };

        foreach (var entry in diagnostics.Take(20))
        {
            lines.Add(Line(entry.Level, entry.Source, $"  [{entry.ObservedAt}] {entry.Message}", ts));
        }

        if (diagnostics.Count == 0)
        {
            lines.Add(Line("info", "console", "  (no diagnostics)", ts));
        }

        return Ok("diagnostics", lines);
    }

    private async Task<ConsoleCommandRunResponse> RunClearDiagnosticsAsync(CancellationToken cancellationToken)
    {
        await _runtime.ClearDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
        var ts = NowString();
        var lines = new List<ConsoleCommandLine>
        {
            Line("ok", "console", "Diagnostics buffer cleared.", ts),
        };

        return Ok("clear-diagnostics", lines);
    }

    private ConsoleCommandRunResponse UnknownCommand(string command)
    {
        var ts = NowString();
        return new ConsoleCommandRunResponse
        {
            Command = command,
            Status = "error",
            ErrorMessage = $"Unknown command '{command}'. Type 'help' to list available commands.",
            Lines =
            [
                Line("err", "console", $"Unknown command '{command}'. Type 'help' to list available commands.", ts),
            ],
        };
    }

    private static ConsoleCommandRunResponse Ok(string command, IReadOnlyList<ConsoleCommandLine> lines)
    {
        return new ConsoleCommandRunResponse
        {
            Command = command,
            Status = "success",
            Lines = lines,
        };
    }

    private static ConsoleCommandRunResponse Error(string command, string errorMessage, IReadOnlyList<ConsoleCommandLine> lines)
    {
        return new ConsoleCommandRunResponse
        {
            Command = command,
            Status = "error",
            ErrorMessage = errorMessage,
            Lines = lines,
        };
    }

    private static ConsoleCommandLine Line(string level, string source, string message, string timestamp)
    {
        return new ConsoleCommandLine
        {
            Level = level,
            Source = source,
            Timestamp = timestamp,
            Message = message,
        };
    }

    private string NowString()
    {
        return _now().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
    }
}
