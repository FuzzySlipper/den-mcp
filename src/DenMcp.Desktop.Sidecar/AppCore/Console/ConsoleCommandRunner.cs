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
            Description = "Trigger a Den operator refresh cycle (projects, workspaces, and snapshot publication).",
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
            Description = "Publish latest local git/diff/session snapshots via the runtime snapshot publish cycle.",
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
            "help" => await RunHelpAsync(onProgress, cancellationToken).ConfigureAwait(false),
            "refresh" => await RunRefreshAsync(onProgress, cancellationToken).ConfigureAwait(false),
            "inspect-connection" => await RunInspectConnectionAsync(onProgress, cancellationToken).ConfigureAwait(false),
            "publish-snapshot" => await RunPublishSnapshotAsync(onProgress, cancellationToken).ConfigureAwait(false),
            "list-sessions" => await RunListSessionsAsync(onProgress, cancellationToken).ConfigureAwait(false),
            "git-status" => await RunGitStatusAsync(request, onProgress, cancellationToken).ConfigureAwait(false),
            "diagnostics" => await RunDiagnosticsAsync(onProgress, cancellationToken).ConfigureAwait(false),
            "clear-diagnostics" => await RunClearDiagnosticsAsync(onProgress, cancellationToken).ConfigureAwait(false),
            _ => await UnknownCommandAsync(request.Command, onProgress, cancellationToken).ConfigureAwait(false),
        };
    }

    private async Task<ConsoleCommandRunResponse> RunHelpAsync(ConsoleCommandProgressCallback? onProgress = null, CancellationToken cancellationToken = default)
    {
        var ts = NowString();
        var lines = new List<ConsoleCommandLine>();
        await EmitAsync(onProgress, Line("ok", "console", $"Available commands ({BuiltInCommands.Count}):", ts), lines, cancellationToken).ConfigureAwait(false);

        foreach (var cmd in BuiltInCommands)
        {
            var targetHint = cmd.NeedsTarget ? " [needs target]" : "";
            await EmitAsync(onProgress, Line("info", "console", $"  {cmd.Name,-20} {cmd.DisplayName,-15} {cmd.Description}{targetHint}", ts), lines, cancellationToken).ConfigureAwait(false);
        }

        return Ok("help", lines);
    }

    private async Task<ConsoleCommandRunResponse> RunRefreshAsync(ConsoleCommandProgressCallback? onProgress = null, CancellationToken cancellationToken = default)
    {
        var ts = NowString();
        var lines = new List<ConsoleCommandLine>();
        await EmitAsync(onProgress, Line("info", "console", "Triggering operator refresh cycle...", ts), lines, cancellationToken).ConfigureAwait(false);

        try
        {
            await _runtime.RefreshAsync(cancellationToken).ConfigureAwait(false);
            var status = await _runtime.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            await EmitAsync(onProgress, Line("ok", "console", $"Refresh complete. {status.ProjectCount} projects, {status.WorkspaceCount} workspaces, {status.LocalSnapshotCount} snapshots.", ts), lines, cancellationToken).ConfigureAwait(false);
            return Ok("refresh", lines);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // Cancellation propagates; do not turn it into an error response.
        }
        catch (Exception ex)
        {
            await EmitAsync(onProgress, Line("err", "console", $"Refresh failed: {ex.Message}", ts), lines, cancellationToken).ConfigureAwait(false);
            return Error("refresh", ex.Message, lines);
        }
    }

    private async Task<ConsoleCommandRunResponse> RunPublishSnapshotAsync(ConsoleCommandProgressCallback? onProgress = null, CancellationToken cancellationToken = default)
    {
        var ts = NowString();
        var lines = new List<ConsoleCommandLine>();
        await EmitAsync(onProgress, Line("info", "console", "Publishing latest snapshots (refresh-backed publish cycle)...", ts), lines, cancellationToken).ConfigureAwait(false);
        await EmitAsync(onProgress, Line("info", "console", "Snapshot publication uses the runtime refresh cycle to discover Den scopes, inspect local git/session state, and publish git/diff/session snapshots consistently.", ts), lines, cancellationToken).ConfigureAwait(false);

        try
        {
            await _runtime.PublishSnapshotsAsync(cancellationToken).ConfigureAwait(false);
            var status = await _runtime.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            await EmitAsync(onProgress, Line("ok", "console", $"Publish snapshot complete. {status.LocalSnapshotCount} git snapshots, {status.LocalSessionSnapshotCount} session snapshots.", ts), lines, cancellationToken).ConfigureAwait(false);
            return Ok("publish-snapshot", lines);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // Cancellation propagates; do not turn it into an error response.
        }
        catch (Exception ex)
        {
            await EmitAsync(onProgress, Line("err", "console", $"Publish snapshot failed: {ex.Message}", ts), lines, cancellationToken).ConfigureAwait(false);
            return Error("publish-snapshot", ex.Message, lines);
        }
    }

    private async Task<ConsoleCommandRunResponse> RunInspectConnectionAsync(ConsoleCommandProgressCallback? onProgress = null, CancellationToken cancellationToken = default)
    {
        var status = await _runtime.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var ts = NowString();
        var conn = status.DenConnection;

        var lines = new List<ConsoleCommandLine>();
        await EmitAsync(onProgress, Line("info", "console", "--- Den Connection Status ---", ts), lines, cancellationToken).ConfigureAwait(false);
        await EmitAsync(onProgress, Line("info", "den", $"State: {conn.State}", ts), lines, cancellationToken).ConfigureAwait(false);
        await EmitAsync(onProgress, Line("info", "den", $"URL:   {status.DenBaseUrl}", ts), lines, cancellationToken).ConfigureAwait(false);
        await EmitAsync(onProgress, Line("info", "den", $"Phase: {status.Phase}", ts), lines, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(conn.Message))
            await EmitAsync(onProgress, Line("info", "den", $"Message: {conn.Message}", ts), lines, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(conn.LastSuccessAt))
            await EmitAsync(onProgress, Line("info", "den", $"Last success: {conn.LastSuccessAt}", ts), lines, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(conn.LastFailureAt))
            await EmitAsync(onProgress, Line("warn", "den", $"Last failure: {conn.LastFailureAt}", ts), lines, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(conn.NextRetryAt))
            await EmitAsync(onProgress, Line("info", "den", $"Next retry: {conn.NextRetryAt}", ts), lines, cancellationToken).ConfigureAwait(false);

        await EmitAsync(onProgress, Line("info", "console", "--- Observers ---", ts), lines, cancellationToken).ConfigureAwait(false);
        foreach (var observer in status.ObserverStatuses)
        {
            var warnPart = observer.WarningCount > 0 ? $" ({observer.WarningCount} warnings)" : "";
            await EmitAsync(onProgress, Line("info", observer.Kind, $"  {observer.Kind}: {observer.State}{warnPart}", ts), lines, cancellationToken).ConfigureAwait(false);
        }

        return Ok("inspect-connection", lines);
    }

    private async Task<ConsoleCommandRunResponse> RunListSessionsAsync(ConsoleCommandProgressCallback? onProgress = null, CancellationToken cancellationToken = default)
    {
        var ts = NowString();
        var sessions = _sessionRegistry.List();
        var lines = new List<ConsoleCommandLine>();
        await EmitAsync(onProgress, Line("info", "console", $"Operator sessions ({sessions.Count}):", ts), lines, cancellationToken).ConfigureAwait(false);

        foreach (var session in sessions)
        {
            await EmitAsync(onProgress, Line("info", "session", $"  {session.SessionId} — {session.Kind ?? "unknown"} ({session.Status ?? "unknown"})", ts), lines, cancellationToken).ConfigureAwait(false);
        }

        if (sessions.Count == 0)
        {
            await EmitAsync(onProgress, Line("info", "session", "  (no sessions observed)", ts), lines, cancellationToken).ConfigureAwait(false);
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
        await EmitAsync(onProgress, Line("info", "console", $"Git status for {filtered.Count} workspace(s):", ts), lines, cancellationToken).ConfigureAwait(false);

        foreach (var snapshot in filtered)
        {
            var req = snapshot.Request;
            var branch = req.Branch ?? "(detached)";
            var dirty = req.DirtyCounts.Total > 0 ? $"±{req.DirtyCounts.Total}" : "clean";
            var head = req.HeadSha is { Length: > 8 } ? req.HeadSha[..8] : req.HeadSha ?? "—";
            await EmitAsync(onProgress, Line("info", "git", $"  {snapshot.Scope.ProjectId}/{snapshot.Scope.RootPath}", ts), lines, cancellationToken).ConfigureAwait(false);
            await EmitAsync(onProgress, Line("info", "git", $"    branch: {branch}  head: {head}  dirty: {dirty}", ts), lines, cancellationToken).ConfigureAwait(false);

            if (req.Warnings.Count > 0)
            {
                foreach (var warning in req.Warnings.Take(3))
                {
                    await EmitAsync(onProgress, Line("warn", "git", $"    warning: {warning}", ts), lines, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        if (filtered.Count == 0)
        {
            await EmitAsync(onProgress, Line("info", "git", "  (no matching workspaces)", ts), lines, cancellationToken).ConfigureAwait(false);
        }

        return Ok("git-status", lines);
    }

    private async Task<ConsoleCommandRunResponse> RunDiagnosticsAsync(ConsoleCommandProgressCallback? onProgress = null, CancellationToken cancellationToken = default)
    {
        var status = await _runtime.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var ts = NowString();
        var diagnostics = status.Diagnostics;
        var lines = new List<ConsoleCommandLine>();
        await EmitAsync(onProgress, Line("info", "console", $"Recent diagnostics ({diagnostics.Count} entries):", ts), lines, cancellationToken).ConfigureAwait(false);

        foreach (var entry in diagnostics.Take(20))
        {
            await EmitAsync(onProgress, Line(entry.Level, entry.Source, $"  [{entry.ObservedAt}] {entry.Message}", ts), lines, cancellationToken).ConfigureAwait(false);
        }

        if (diagnostics.Count == 0)
        {
            await EmitAsync(onProgress, Line("info", "console", "  (no diagnostics)", ts), lines, cancellationToken).ConfigureAwait(false);
        }

        return Ok("diagnostics", lines);
    }

    private async Task<ConsoleCommandRunResponse> RunClearDiagnosticsAsync(ConsoleCommandProgressCallback? onProgress = null, CancellationToken cancellationToken = default)
    {
        await _runtime.ClearDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
        var ts = NowString();
        var lines = new List<ConsoleCommandLine>();
        await EmitAsync(onProgress, Line("ok", "console", "Diagnostics buffer cleared.", ts), lines, cancellationToken).ConfigureAwait(false);

        return Ok("clear-diagnostics", lines);
    }

    private async Task<ConsoleCommandRunResponse> UnknownCommandAsync(string command, ConsoleCommandProgressCallback? onProgress = null, CancellationToken cancellationToken = default)
    {
        var ts = NowString();
        var lines = new List<ConsoleCommandLine>();
        await EmitAsync(onProgress, Line("err", "console", $"Unknown command '{command}'. Type 'help' to list available commands.", ts), lines, cancellationToken).ConfigureAwait(false);

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
    private static async ValueTask EmitAsync(ConsoleCommandProgressCallback? onProgress, ConsoleCommandLine line, List<ConsoleCommandLine> lines, CancellationToken cancellationToken)
    {
        lines.Add(line);
        if (onProgress is null)
        {
            return;
        }

        // Progress reporting is best-effort and async-safe. Await the callback so
        // bridge progress publishers can flush frames without blocking synchronously;
        // keep collected output intact if a progress consumer fails.
        try
        {
            await onProgress(line, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Progress reporting is best-effort; output lines are always collected.
        }
    }

    private string NowString()
    {
        return _now().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
    }
}
