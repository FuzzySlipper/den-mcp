using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DenMcp.Desktop.Sidecar;

public sealed record TmuxSessionIdentity
{
    public required string SessionName { get; init; }
    public required string SessionId { get; init; }
    public required string BackendRef { get; init; }
    public required string SocketHash { get; init; }
}

public static partial class TmuxSessionNaming
{
    public const string SessionPrefix = "den";
    private const int MaxSessionNameLength = 80;

    public static TmuxSessionIdentity Create(
        string sourceInstanceId,
        string? projectId,
        long? taskId,
        string? workspaceId,
        string? title = null,
        string? socketName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceInstanceId);

        var socketHash = ShortHash(socketName ?? "default");
        var sourceHash = ShortHash(sourceInstanceId);
        var parts = new List<string>
        {
            SessionPrefix,
            sourceHash,
            Slug(projectId, "project"),
        };

        if (taskId is { } id)
        {
            parts.Add($"task{id}");
        }

        if (!string.IsNullOrWhiteSpace(workspaceId))
        {
            parts.Add($"ws{ShortHash(workspaceId)}");
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            parts.Add(Slug(title, "session"));
        }

        var baseName = string.Join('-', parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        var suffix = ShortHash($"{sourceInstanceId}|{projectId}|{taskId}|{workspaceId}|{title}|{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
        var sessionName = TrimTmuxName($"{baseName}-{suffix}");
        return FromSessionName(sessionName, socketName);
    }

    public static TmuxSessionIdentity FromSessionName(string sessionName, string? socketName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionName);
        var socketHash = ShortHash(socketName ?? "default");
        var safeName = sessionName.Trim();
        return new TmuxSessionIdentity
        {
            SessionName = safeName,
            SessionId = $"tmux-session:{socketHash}:{safeName}",
            BackendRef = $"tmux://{socketHash}/{safeName}",
            SocketHash = socketHash,
        };
    }

    public static bool LooksManaged(string sessionName)
    {
        return sessionName.StartsWith(SessionPrefix + "-", StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds opaque display/copy text for operators who want to attach from an
    /// external terminal. Den Desktop must not execute this string directly.
    /// </summary>
    public static string ExternalAttachCommand(string sessionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionName);
        return $"tmux attach-session -t {ShellQuote(sessionName)}";
    }

    private static string TrimTmuxName(string value)
    {
        if (value.Length <= MaxSessionNameLength)
        {
            return value;
        }

        var hash = ShortHash(value);
        return value[..Math.Min(value.Length, MaxSessionNameLength - hash.Length - 1)] + "-" + hash;
    }

    private static string Slug(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var lowered = value.Trim().ToLowerInvariant();
        var slug = UnsafeSlugChars().Replace(lowered, "-").Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? fallback : slug;
    }

    private static string ShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
    }

    private static string ShellQuote(string value)
    {
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    [GeneratedRegex("[^a-z0-9_.-]+", RegexOptions.CultureInvariant)]
    private static partial Regex UnsafeSlugChars();
}
