using System.Security.Cryptography;
using System.Text;

namespace DenMcp.Desktop.Sidecar;

/// <summary>
/// Shared reader for bounded structured OperatorSession activity summaries.
///
/// Activity cursors are content-identity cursors over the currently retained
/// RecentActivity snapshot, not list indexes. A cursor remains valid across
/// registry refreshes as long as the referenced activity item is still present
/// in the bounded snapshot. If the referenced item has fallen out of the
/// snapshot, reads restart from the current snapshot beginning rather than
/// applying a stale index that could skip retained items.
/// </summary>
public static class OperatorSessionActivityReader
{
    public const string CursorPrefix = "act_v1_";

    public static TerminalReadActivityResponse Read(OperatorSession session, string? afterCursor, int limit)
    {
        ArgumentNullException.ThrowIfNull(session);

        var boundedLimit = Math.Clamp(limit, 1, 200);
        var allItems = session.RecentActivity;
        var startIndex = ResolveStartIndex(allItems, afterCursor);
        var items = allItems.Skip(startIndex).Take(boundedLimit).ToList();
        var responseItems = items.Select(a => new TerminalActivityItem
        {
            Kind = a.Kind,
            Role = a.Role,
            Tool = a.Tool,
            Summary = a.Summary,
            Timestamp = a.Timestamp,
        }).ToList();

        var nextCursor = items.Count > 0
            ? CreateCursor(allItems, startIndex + items.Count - 1)
            : startIndex == allItems.Count ? afterCursor : null;

        return new TerminalReadActivityResponse
        {
            SessionId = session.SessionId,
            Items = responseItems,
            NextCursor = nextCursor,
            Truncated = startIndex + items.Count < allItems.Count,
        };
    }

    private static int ResolveStartIndex(IReadOnlyList<OperatorSessionActivityItem> items, string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return 0;
        }

        if (TryParseActivityCursor(cursor, out var fingerprint, out var occurrence))
        {
            var seen = 0;
            for (var index = 0; index < items.Count; index++)
            {
                if (!string.Equals(Fingerprint(items[index]), fingerprint, StringComparison.Ordinal))
                {
                    continue;
                }

                seen++;
                if (seen == occurrence)
                {
                    return index + 1;
                }
            }

            return 0;
        }

        if (TryParseLegacyIndexCursor(cursor, out var legacyIndex))
        {
            return (int)Math.Clamp(legacyIndex, 0, items.Count);
        }

        return 0;
    }

    private static string CreateCursor(IReadOnlyList<OperatorSessionActivityItem> items, int itemIndex)
    {
        var fingerprint = Fingerprint(items[itemIndex]);
        var occurrence = 0;
        for (var index = 0; index <= itemIndex; index++)
        {
            if (string.Equals(Fingerprint(items[index]), fingerprint, StringComparison.Ordinal))
            {
                occurrence++;
            }
        }

        return $"{CursorPrefix}{occurrence:D12}_{fingerprint}";
    }

    private static bool TryParseActivityCursor(string cursor, out string fingerprint, out int occurrence)
    {
        fingerprint = string.Empty;
        occurrence = 0;

        if (!cursor.StartsWith(CursorPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var payload = cursor[CursorPrefix.Length..];
        var separator = payload.IndexOf('_', StringComparison.Ordinal);
        if (separator <= 0 || separator == payload.Length - 1)
        {
            return false;
        }

        if (!int.TryParse(payload[..separator], out occurrence) || occurrence <= 0)
        {
            return false;
        }

        fingerprint = payload[(separator + 1)..];
        return fingerprint.Length > 0;
    }

    private static bool TryParseLegacyIndexCursor(string cursor, out long index)
    {
        index = 0;
        if (!cursor.StartsWith("cur_", StringComparison.Ordinal))
        {
            return false;
        }

        return long.TryParse(cursor[4..], out index);
    }

    private static string Fingerprint(OperatorSessionActivityItem item)
    {
        var builder = new StringBuilder();
        AppendField(builder, item.Timestamp);
        AppendField(builder, item.Kind);
        AppendField(builder, item.Role);
        AppendField(builder, item.Tool);
        AppendField(builder, item.Summary);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash, 0, 16);
    }

    private static void AppendField(StringBuilder builder, string? value)
    {
        builder.Append(value ?? "\u0000");
        builder.Append('\u001F');
    }
}
