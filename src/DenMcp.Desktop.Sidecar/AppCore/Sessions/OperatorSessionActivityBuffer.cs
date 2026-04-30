namespace DenMcp.Desktop.Sidecar;

/// <summary>
/// Bounded, in-memory buffer for terminal/activity stream output chunks.
/// Supports replay from cursor, backpressure tracking, and automatic dropping
/// of oldest chunks when capacity is exceeded.
///
/// Each chunk carries a monotonic <see cref="TerminalOutputChunk.Sequence"/> and
/// a string cursor for stable replay reference.
/// </summary>
public sealed class OperatorSessionActivityBuffer
{
    public const int DefaultMaxBytes = 1_048_576; // 1 MiB
    public const int DefaultMaxChunks = 2000;
    public const int DefaultOutputChunkMaxBytes = 65_536; // 64 KiB
    public const int DefaultMaxQueuedSubscriberBytes = 262_144; // 256 KiB

    private readonly int _maxBytes;
    private readonly int _maxChunks;
    private readonly int _outputChunkMaxBytes;
    private readonly int _maxQueuedSubscriberBytes;
    private readonly LinkedList<TerminalOutputChunk> _chunks = new();
    private readonly object _lock = new();
    private long _nextSequence;
    private int _totalBytes;
    private long _droppedBytesBeforeStart;

    public OperatorSessionActivityBuffer(
        int maxBytes = DefaultMaxBytes,
        int maxChunks = DefaultMaxChunks,
        int outputChunkMaxBytes = DefaultOutputChunkMaxBytes,
        int maxQueuedSubscriberBytes = DefaultMaxQueuedSubscriberBytes)
    {
        _maxBytes = maxBytes > 0 ? maxBytes : DefaultMaxBytes;
        _maxChunks = maxChunks > 0 ? maxChunks : DefaultMaxChunks;
        _outputChunkMaxBytes = outputChunkMaxBytes > 0 ? outputChunkMaxBytes : DefaultOutputChunkMaxBytes;
        _maxQueuedSubscriberBytes = maxQueuedSubscriberBytes > 0 ? maxQueuedSubscriberBytes : DefaultMaxQueuedSubscriberBytes;
    }

    public int OutputChunkMaxBytes => _outputChunkMaxBytes;
    public int MaxQueuedSubscriberBytes => _maxQueuedSubscriberBytes;

    /// <summary>
    /// Append a raw output chunk. Automatically splits oversized chunks into
    /// multiple sequential entries per R945-2 (each <= 64 KiB).
    /// Returns the list of appended chunks with their assigned sequences.
    /// </summary>
    public IReadOnlyList<TerminalOutputChunk> Append(byte[] data, string? origin = "live", int? cols = null, int? rows = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0)
        {
            return [];
        }

        lock (_lock)
        {
            var chunks = SplitOversizedLocked(data, origin, cols, rows);
            foreach (var chunk in chunks)
            {
                _chunks.AddLast(chunk);
                _totalBytes += chunk.ByteCount;
            }

            EvictLocked();
            return chunks;
        }
    }

    /// <summary>
    /// Read chunks after a given cursor. Returns only chunks with sequence &gt; cursor.
    /// Cursor is the last sequence the consumer has acked.
    /// </summary>
    public ActivityBufferReadResult ReadAfter(long afterSequence, int limit = 200)
    {
        lock (_lock)
        {
            var items = _chunks
                .Where(c => c.Sequence > afterSequence)
                .Take(limit)
                .ToList();

            var nextCursor = items.Count > 0 ? items[^1].Sequence : afterSequence;
            var replayGap = afterSequence > 0 && _chunks.Count > 0 && _chunks.First!.Value.Sequence > afterSequence + 1;

            return new ActivityBufferReadResult
            {
                Chunks = items,
                NextCursor = nextCursor,
                ReplayGap = replayGap,
                DroppedBytesBeforeStart = _droppedBytesBeforeStart,
                AvailableFrom = _chunks.Count > 0 ? _chunks.First!.Value.Sequence : 0,
            };
        }
    }

    /// <summary>
    /// Current buffer stats for diagnostics.
    /// </summary>
    public ActivityBufferStats GetStats()
    {
        lock (_lock)
        {
            return new ActivityBufferStats
            {
                ChunkCount = _chunks.Count,
                TotalBytes = _totalBytes,
                NextSequence = _nextSequence,
                DroppedBytesBeforeStart = _droppedBytesBeforeStart,
                OldestSequence = _chunks.Count > 0 ? _chunks.First!.Value.Sequence : 0,
                NewestSequence = _chunks.Count > 0 ? _chunks.Last!.Value.Sequence : 0,
            };
        }
    }

    private List<TerminalOutputChunk> SplitOversizedLocked(byte[] data, string? origin, int? cols, int? rows)
    {
        var chunks = new List<TerminalOutputChunk>();
        var offset = 0;
        while (offset < data.Length)
        {
            var remaining = data.Length - offset;
            var chunkSize = Math.Min(remaining, _outputChunkMaxBytes);
            var chunkData = new byte[chunkSize];
            Buffer.BlockCopy(data, offset, chunkData, 0, chunkSize);

            chunks.Add(new TerminalOutputChunk
            {
                Sequence = ++_nextSequence,
                Data = chunkData,
                ByteCount = chunkSize,
                Origin = origin,
                Cols = cols,
                Rows = rows,
                Truncated = chunkSize < remaining,
            });

            offset += chunkSize;
        }

        return chunks;
    }

    private void EvictLocked()
    {
        while ((_chunks.Count > _maxChunks || _totalBytes > _maxBytes) && _chunks.Count > 0)
        {
            var first = _chunks.First!;
            _chunks.RemoveFirst();
            _totalBytes -= first.Value.ByteCount;
            _droppedBytesBeforeStart += first.Value.ByteCount;
        }
    }
}

public sealed record TerminalOutputChunk
{
    public required long Sequence { get; init; }
    public required byte[] Data { get; init; }
    public required int ByteCount { get; init; }
    public string? Origin { get; init; }
    public int? Cols { get; init; }
    public int? Rows { get; init; }
    public bool Truncated { get; init; }
    public bool Redacted { get; init; }

    public string ChunkId => $"chunk_{Sequence:D12}";
    public string StreamCursor => $"cur_{Sequence:D12}";
}

public sealed record ActivityBufferReadResult
{
    public required IReadOnlyList<TerminalOutputChunk> Chunks { get; init; }
    public required long NextCursor { get; init; }
    public bool ReplayGap { get; init; }
    public long DroppedBytesBeforeStart { get; init; }
    public long AvailableFrom { get; init; }
}

public sealed record ActivityBufferStats
{
    public int ChunkCount { get; init; }
    public int TotalBytes { get; init; }
    public long NextSequence { get; init; }
    public long DroppedBytesBeforeStart { get; init; }
    public long OldestSequence { get; init; }
    public long NewestSequence { get; init; }
}
