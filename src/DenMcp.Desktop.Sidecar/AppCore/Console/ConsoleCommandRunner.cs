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

    public async Task<ConsoleCommandRunResponse> RunCommandAsync(ConsoleCommandRunRequest request, ConsoleCommandProgressCallback? onProgress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Command switch
        {
            "help" => RunHelp(onProgress),
            "refresh" => await RunRefreshAsync(onProgress, cancellationToken).ConfigureAwait(false),
            "inspect-connection" => await RunInspectConnectionAsync(onProgress, cancellationToken).ConfigureAwait(false),
            "publish-snapshot" => await RunRefreshAsync(onProgress, cancellationToken).ConfigureAwait(false),
            "list-sessions" => RunListSessions(onProgress),
            "git-status" => await RunGitStatusAsync(request, onProgress, cancellationToken).ConfigureAwait(false),
            "diagnostics" => await RunDiagnosticsAsync(onProgress, cancellationToken).ConfigureAwait(false),
            "clear-diagnostics" => await RunClearDiagnosticsAsync(onProgress, cancellationToken).ConfigureAwait(false),
            _ => UnknownCommand(request.Command, onProgress),
        };
    }

    private ConsoleCommandRunResponse RunHelp(ConsoleCommandProgressCallback? onProgress = null)
    {
        var ts = NowString();
        var lines = new List<ConsoleCommandLine>();
        Emit(onProgress, Line("ok", "console", $"Available commands ({BuiltInCommands.Count}):", ts), ref lines, default);

        foreach (var cmd in BuiltInCommands)
        {
            var targetHint = cmd.NeedsTarget ? " [needs target]" : "";
            Emit(onProgress, Line("info", "console", $"  {cmd.Name,-20} {cmd.DisplayName,-15} {cmd.Description}{targetHint}", ts), ref lines, default);
        }

        return Ok("help", lines);
    }

    private async Task<ConsoleCommandRunResponse> RunRefreshAsync(ConsoleCommandProgressCallback? onProgress = null, CancellationToken cancellationToken = default)
    {
        var ts = NowString();
        var lines = new List<ConsoleCommandLine>();
        Emit(onProgress, Line("info", "console", "Triggering operator refresh cycle...", ts), ref lines, cancellationToken);

        try
        {
            await _runtime.RefreshAsync(cancellationToken).ConfigureAwait(false);
            var status = await _runtime.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            Emit(onProgress, Line("ok", "console", $"Refresh complete. {status.ProjectCount} projects, {status.WorkspaceCount} workspaces, {status.LocalSnapshotCount} snapshots.", ts), ref lines, cancellationToken);
            return Ok("refresh", lines);
        }
        catch (Exception ex)
        {
            Emit(onProgress, Line("err", "console", $"Refresh failed: {ex.Message}", ts), ref lines, cancellationToken);
            return Error("refresh", ex.Message, lines);
        }
    }

    private async Task<ConsoleCommandRunResponse> RunInspectConnectionAsync(ConsoleCommandProgressCallback? onProgress = null, CancellationToken cancellationToken = default)
    {
        var status = await _runtime.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var ts = NowString();
        var conn = status.DenConnection;

        var lines = new List<ConsoleCommandLine>();
        Emit(onProgress, Line("info", "console", "--- Den Connection Status ---", ts), ref lines, cancellationToken);
        Emit(onProgress, Line("info", "den", $"State: {conn.State}", ts), ref lines, cancellationToken);
        Emit(onProgress, Line("info", "den", $"URL:   {status.DenBaseUrl}", ts), ref lines, cancellationToken);
        Emit(onProgress, Line("info", "den", $"Phase: {status.Phase}", ts), ref lines, cancellationToken);

        if (!string.IsNullOrWhiteSpace(conn.Message))
            Emit(onProgress, Line("info", "den", $"Message: {conn.Message}", ts), ref lines, cancellationToken);
        if (!string.IsNullOrWhiteSpace(conn.LastSuccessAt))
            Emit(onProgress, Line("info", "den", $"Last success: {conn.LastSuccessAt}", ts), ref lines, cancellationToken);
        if (!string.IsNullOrWhiteSpace(conn.LastFailureAt))
            Emit(onProgress, Line("warn", "den", $"Last failure: {conn.LastFailureAt}", ts), ref lines, cancellationToken);
        if (!string.IsNullOrWhiteSpace(conn.NextRetryAt))
            Emit(onProgress, Line("info", "den", $"Next retry: {conn.NextRetryAt}", ts), ref lines, cancellationToken);

        Emit(onProgress, Line("info", "console", "--- Observers ---", ts), ref lines, cancellationToken);
        foreach (var observer in status.ObserverStatuses)
        {
            var warnPart = observer.WarningCount > 0 ? $" ({observer.WarningCount} warnings)" : "";
            Emit(onProgress, Line("info", observer.Kind, $"  {observer.Kind}: {observer.State}{warnPart}", ts), ref lines, cancellationToken);
        }

        return Ok("inspect-connection", lines);
    }

    private ConsoleCommandRunResponse RunListSessions(ConsoleCommandProgressCallback? onProgress = null)
    {
        var ts = NowString();
        var sessions = _sessionRegistry.List();
        var lines = new List<ConsoleCommandLine>();
        Emit(onProgress, Line("info", "console", $"Operator sessions ({sessions.Count}):", ts), ref lines, default);

        foreach (var session in sessions)
        {
            Emit(onProgress, Line("info", "session", $"  {session.SessionId} — {session.Kind ?? "unknown"} ({session.Status ?? "unknown"})", ts), ref lines, default);
        }

        if (sessions.Count == 0)
        {
            Emit(onProgress, Line("info", "session", "  (no sessions observed)", ts), ref lines, default);
        }

        return Ok("list-sessions", lines);
    }

    private async Task<ConsoleCommandRunResponse> RunGitStatusAsync(ConsoleCommandRunRequest request, ConsoleCommandProgressCallback? onProgress = null, CancellationToken cancellationToken = default)
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

        var lines = new List<ConsoleCommandLine>();
        Emit(onProgress, Line("info", "console", $"Git status for {filtered.Count} workspace(s):", ts), ref lines, cancellationToken);

        foreach (var snapshot in filtered)
        {
            var req = snapshot.Request;
            var branch = req.Branch ?? "(detached)";
            var dirty = req.DirtyCounts.Total > 0 ? $"±{req.DirtyCounts.Total}" : "clean";
            var head = req.HeadSha is { Length: > 8 } ? req.HeadSha[..8] : req.HeadSha ?? "—";
            Emit(onProgress, Line("info", "git", $"  {snapshot.Scope.ProjectId}/{snapshot.Scope.RootPath}", ts), ref lines, cancellationToken);
            Emit(onProgress, Line("info", "git", $"    branch: {branch}  head: {head}  dirty: {dirty}", ts), ref lines, cancellationToken);

            if (req.Warnings.Count > 0)
            {
                foreach (var warning in req.Warnings.Take(3))
                {
                    Emit(onProgress, Line("warn", "git", $"    warning: {warning}", ts), ref lines, cancellationToken);
                }
            }
        }

        if (filtered.Count == 0)
        {
            Emit(onProgress, Line("info", "git", "  (no matching workspaces)", ts), ref lines, cancellationToken);
        }

        return Ok("git-status", lines);
    }

    private async Task<ConsoleCommandRunResponse> RunDiagnosticsAsync(ConsoleCommandProgressCallback? onProgress = null, CancellationToken cancellationToken = default)
    {
        var status = await _runtime.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var ts = NowString();
        var diagnostics = status.Diagnostics;
        var lines = new List<ConsoleCommandLine>();
        Emit(onProgress, Line("info", "console", $"Recent diagnostics ({diagnostics.Count} entries):", ts), ref lines, cancellationToken);

        foreach (var entry in diagnostics.Take(20))
        {
            Emit(onProgress, Line(entry.Level, entry.Source, $"  [{entry.ObservedAt}] {entry.Message}", ts), ref lines, cancellationToken);
        }

        if (diagnostics.Count == 0)
        {
            Emit(onProgress, Line("info", "console", "  (no diagnostics)", ts), ref lines, cancellationToken);
        }

        return Ok("diagnostics", lines);
    }

    private async Task<ConsoleCommandRunResponse> RunClearDiagnosticsAsync(ConsoleCommandProgressCallback? onProgress = null, CancellationToken cancellationToken = default)
    {
        await _runtime.ClearDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
        var ts = NowString();
        var lines = new List<ConsoleCommandLine>();
        Emit(onProgress, Line("ok", "console", "Diagnostics buffer cleared.", ts), ref lines, cancellationToken);

        return Ok("clear-diagnostics", lines);
    }

    private ConsoleCommandRunResponse UnknownCommand(string command, ConsoleCommandProgressCallback? onProgress = null)
    {
        var ts = NowString();
        var lines = new List<ConsoleCommandLine>();
        Emit(onProgress, Line("err", "console", $"Unknown command '{command}'. Type 'help' to list available commands.", ts), ref lines, default);

        return new ConsoleCommandRunResponse
        {
            Command = command,
            Status = "error",
            ErrorMessage = $"Unknown command '{command}'. Type 'help' to list available commands.",
            Lines = lines,
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

    /// <summary>
    /// Emit a structured line: record it in the response list and, if a progress callback
    /// is provided, forward it as a progress event for streaming consumption.
    /// </summary>
    private void Emit(ConsoleCommandProgressCallback? onProgress, ConsoleCommandLine line, ref List<ConsoleCommandLine> lines, CancellationToken cancellationToken)
    {
        lines ??= [];
        lines.Add(line);
        if (onProgress is not null)
        {
            // Fire-and-forget progress reporting. The callback is expected to complete
            // quickly (writing to a channel/bridge progress publisher). Exceptions are
            // swallowed so that output collection is not disrupted by a slow consumer.
            try
            {
                onProgress(line, cancellationToken).AsTask().GetAwaiter().GetResult();
            }
            catch
            {
                // Progress reporting is best-effort; output lines are always collected.
            }
        }
    }

    private string NowString()
    {
        return _now().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
    }
}
