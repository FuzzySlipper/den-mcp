using System.Text.Json;

namespace DenMcp.Desktop.Sidecar;

/// <summary>
/// Local registry for OperatorSession instances. Thread-safe.
///
/// Manages the authoritative local set of OperatorSession records and provides
/// lookup, projection, and capability computation.
/// </summary>
public sealed class OperatorSessionRegistry
{
    private readonly Dictionary<string, OperatorSession> _sessions = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private readonly Func<DateTime> _now;
    private long _globalSequence;

    public OperatorSessionRegistry(Func<DateTime>? now = null)
    {
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Register or update a session in the local registry.
    /// UpdatedAt and Sequence are registry-authoritative and are overwritten
    /// with the local registry clock/sequence on every call. Source-provided
    /// observation timestamps remain in LastObservedAt and LastActivityAt.
    /// </summary>
    public OperatorSession Register(OperatorSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(session.SessionId);
        lock (_lock)
        {
            var now = _now();
            var sequence = ++_globalSequence;
            var merged = session with
            {
                UpdatedAt = now,
                Sequence = sequence,
            };

            _sessions[session.SessionId] = merged;
            return merged;
        }
    }

    /// <summary>
    /// Register or update a session from a legacy Pi artifact snapshot observation.
    /// Computes observe-only capabilities, including can_read_activity when recent activity is available.
    /// Preserves backward-compatible snapshot fields. UpdatedAt remains
    /// registry-authoritative; snapshot ObservedAt is surfaced as LastObservedAt.
    /// </summary>
    public OperatorSession RegisterFromPiSnapshot(LocalSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var req = snapshot.Request;
        var activityItems = ExtractActivityItems(req);
        var canReadActivity = activityItems.Count > 0;

        lock (_lock)
        {
            var now = _now();
            var sequence = ++_globalSequence;
            var normalizedStatus = OperatorSessionStatus.FromLegacyPhase(
                req.CurrentPhase,
                req.ExitedAt is not null ? DateTime.Parse(req.ExitedAt, null, System.Globalization.DateTimeStyles.RoundtripKind) : null);

            var session = new OperatorSession
            {
                SessionId = req.SessionId,
                GlobalRef = $"den-desktop://{req.SourceInstanceId}/{req.SessionId}",
                ParentSessionId = req.ParentSessionId,
                Title = req.Title ?? req.SessionId,
                DisplayName = req.DisplayName ?? req.Role ?? req.Kind ?? "artifact_observer",
                ProjectId = snapshot.ProjectId,
                TaskId = req.TaskId,
                WorkspaceId = req.WorkspaceId,
                Cwd = req.Cwd,
                Kind = OperatorSessionKind.ArtifactObserver,
                Backend = OperatorSessionBackend.PiArtifact,
                Status = normalizedStatus,
                CurrentCommand = req.CurrentCommand,
                AgentIdentity = req.AgentIdentity ?? "pi",
                Role = req.Role,
                Capabilities = OperatorSessionCapabilities.ObserveOnly(
                    "Artifact-observer mode only; no PTY ownership or safe controls are active.",
                    canReadActivity: canReadActivity),
                CreatedAt = ParseDateTime(req.StartedAt) ?? now,
                StartedAt = ParseDateTime(req.StartedAt),
                LastObservedAt = ParseDateTime(req.ObservedAt),
                LastActivityAt = ParseDateTime(req.LastActivityAt),
                ExitedAt = ParseDateTime(req.ExitedAt),
                ExitCode = req.ExitCode is not null ? (int?)req.ExitCode : null,
                SourceInstanceId = req.SourceInstanceId,
                SourceDisplayName = req.SourceDisplayName,
                Warnings = req.Warnings,
                RecentActivity = activityItems,
                UpdatedAt = now,
                Sequence = sequence,
            };

            _sessions[session.SessionId] = session;
            return session;
        }
    }

    /// <summary>
    /// Get a session by id.
    /// </summary>
    public OperatorSession? Get(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_lock)
        {
            return _sessions.TryGetValue(sessionId, out var session) ? session : null;
        }
    }

    /// <summary>
    /// List all sessions, optionally filtered by kind/backend/status.
    /// Returns a snapshot (copy) to avoid holding the lock during enumeration.
    /// </summary>
    public IReadOnlyList<OperatorSession> List(string? kind = null, string? backend = null, string? status = null)
    {
        lock (_lock)
        {
            var all = _sessions.Values.AsEnumerable();

            if (kind is not null)
                all = all.Where(s => string.Equals(s.Kind, kind, StringComparison.OrdinalIgnoreCase));
            if (backend is not null)
                all = all.Where(s => string.Equals(s.Backend, backend, StringComparison.OrdinalIgnoreCase));
            if (status is not null)
                all = all.Where(s => string.Equals(s.Status, status, StringComparison.OrdinalIgnoreCase));

            return all.OrderByDescending(s => s.UpdatedAt).ToList();
        }
    }

    /// <summary>
    /// Remove a session from the registry.
    /// </summary>
    public bool Remove(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_lock)
        {
            return _sessions.Remove(sessionId);
        }
    }

    /// <summary>
    /// Get the total count of sessions in the registry.
    /// </summary>
    public int Count()
    {
        lock (_lock)
        {
            return _sessions.Count;
        }
    }

    /// <summary>
    /// Remove all sessions from the registry. Returns the count removed.
    /// </summary>
    public int Clear()
    {
        lock (_lock)
        {
            var count = _sessions.Count;
            _sessions.Clear();
            return count;
        }
    }

    private static List<OperatorSessionActivityItem> ExtractActivityItems(DesktopSessionSnapshotRequest req)
    {
        var items = new List<OperatorSessionActivityItem>();

        try
        {
            if (req.RecentActivity.ValueKind == JsonValueKind.Object
                && req.RecentActivity.TryGetProperty("items", out var itemsElement)
                && itemsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in itemsElement.EnumerateArray())
                {
                    try
                    {
                        var activityItem = new OperatorSessionActivityItem
                        {
                            Kind = GetString(item, "kind"),
                            Role = GetString(item, "role"),
                            Tool = GetString(item, "tool"),
                            Summary = GetString(item, "summary"),
                            Timestamp = GetString(item, "timestamp"),
                        };
                        items.Add(activityItem);
                    }
                    catch
                    {
                        // skip unparseable activity items
                    }
                }
            }
        }
        catch
        {
            // If activity blob is unparseable, return empty list
        }

        return items;
    }

    private static string? GetString(JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    private static DateTime? ParseDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            return dt;
        return null;
    }
}
