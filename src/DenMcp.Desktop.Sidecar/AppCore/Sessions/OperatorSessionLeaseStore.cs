namespace DenMcp.Desktop.Sidecar;

/// <summary>
/// Local lease store for OperatorSession control authority.
///
/// Manages lease records keyed by backend target (e.g., tmux socket/session/pane).
/// A lease is required before enabling control capabilities for backends that
/// can be seen by multiple desktop instances.
/// </summary>
public sealed class OperatorSessionLeaseStore
{
    public const int DefaultLeaseDurationSeconds = 60;
    public const int DefaultHeartbeatIntervalSeconds = 15;
    public const int DefaultStaleAfterSeconds = 120;

    private readonly Dictionary<string, LeaseRecord> _leases = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private readonly Func<DateTime> _now;

    public OperatorSessionLeaseStore(Func<DateTime>? now = null)
    {
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Try to acquire a lease for a backend target key.
    /// Returns the acquired lease, or null if a conflicting unexpired lease exists.
    /// </summary>
    public LeaseResult TryAcquire(string targetKey, string sessionId, string ownerId, int durationSeconds = DefaultLeaseDurationSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);

        lock (_lock)
        {
            if (_leases.TryGetValue(targetKey, out var existing))
            {
                if (!existing.IsExpired(_now()) && existing.OwnerId != ownerId)
                {
                    return LeaseResult.CreateConflict(existing);
                }

                // Stale or owned by us — replace/upgrade.
                _leases.Remove(targetKey);
            }

            var now = _now();
            var lease = new LeaseRecord
            {
                TargetKey = targetKey,
                SessionId = sessionId,
                OwnerId = ownerId,
                LeaseId = $"lease_{Guid.NewGuid():N}",
                Generation = existing?.Generation + 1 ?? 1,
                AcquiredAt = now,
                ExpiresAt = now.AddSeconds(durationSeconds),
                LastHeartbeatAt = now,
            };

            _leases[targetKey] = lease;
            return LeaseResult.CreateAcquired(lease);
        }
    }

    /// <summary>
    /// Heartbeat an existing lease to extend its expiry.
    /// Returns the updated lease, or null if no lease exists or owner/session mismatch.
    /// </summary>
    public LeaseRecord? Heartbeat(string targetKey, string ownerId, string? sessionId = null, int durationSeconds = DefaultLeaseDurationSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);

        lock (_lock)
        {
            if (!_leases.TryGetValue(targetKey, out var existing))
            {
                return null;
            }

            if (existing.OwnerId != ownerId)
            {
                return null;
            }

            if (sessionId is not null && existing.SessionId != sessionId)
            {
                return null;
            }

            var now = _now();
            var updated = existing with
            {
                LastHeartbeatAt = now,
                ExpiresAt = now.AddSeconds(durationSeconds),
            };

            _leases[targetKey] = updated;
            return updated;
        }
    }

    /// <summary>
    /// Release a lease.
    /// </summary>
    public bool Release(string targetKey, string ownerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);

        lock (_lock)
        {
            if (!_leases.TryGetValue(targetKey, out var existing))
            {
                return false;
            }

            if (existing.OwnerId != ownerId)
            {
                return false;
            }

            return _leases.Remove(targetKey);
        }
    }

    /// <summary>
    /// Get the current lease for a target key, or null if none exists or expired.
    /// </summary>
    public LeaseRecord? GetLease(string targetKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);

        lock (_lock)
        {
            if (!_leases.TryGetValue(targetKey, out var existing))
            {
                return null;
            }

            if (existing.IsExpired(_now()))
            {
                _leases.Remove(targetKey);
                return null;
            }

            return existing;
        }
    }

    /// <summary>
    /// List all live (non-expired) leases.
    /// </summary>
    public IReadOnlyList<LeaseRecord> ListLeases()
    {
        lock (_lock)
        {
            var now = _now();
            var expired = _leases.Where(kv => kv.Value.IsExpired(now)).Select(kv => kv.Key).ToList();
            foreach (var key in expired)
            {
                _leases.Remove(key);
            }

            return _leases.Values.ToList();
        }
    }

    /// <summary>
    /// Clean up expired leases and return count removed.
    /// </summary>
    public int Cleanup()
    {
        lock (_lock)
        {
            var now = _now();
            var expired = _leases.Where(kv => kv.Value.IsExpired(now)).Select(kv => kv.Key).ToList();
            foreach (var key in expired)
            {
                _leases.Remove(key);
            }

            return expired.Count;
        }
    }
}

public sealed record LeaseRecord
{
    public required string TargetKey { get; init; }
    public required string SessionId { get; init; }
    public required string OwnerId { get; init; }
    public required string LeaseId { get; init; }
    public long Generation { get; init; }
    public required DateTime AcquiredAt { get; init; }
    public required DateTime ExpiresAt { get; init; }
    public required DateTime LastHeartbeatAt { get; init; }

    public bool IsExpired(DateTime now) => now >= ExpiresAt;
    public bool IsStale(DateTime now, int staleSeconds = 120) => now >= LastHeartbeatAt.AddSeconds(staleSeconds);
}

public sealed record LeaseResult
{
    public bool Acquired { get; init; }
    public bool Conflict { get; init; }
    public LeaseRecord? Lease { get; init; }
    public LeaseRecord? ConflictingLease { get; init; }
    public string? Message { get; init; }

    public static LeaseResult CreateAcquired(LeaseRecord lease) => new() { Acquired = true, Lease = lease };

    public static LeaseResult CreateConflict(LeaseRecord conflicting) =>
        new()
        {
            Conflict = true,
            ConflictingLease = conflicting,
            Message = $"Lease conflict: target is held by owner '{conflicting.OwnerId}' (lease {conflicting.LeaseId}, generation {conflicting.Generation})",
        };
}
