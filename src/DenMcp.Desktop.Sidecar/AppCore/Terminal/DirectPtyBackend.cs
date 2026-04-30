using System.Runtime.InteropServices;
using Porta.Pty;

namespace DenMcp.Desktop.Sidecar;

public sealed record DirectPtyStartInfo
{
    public required string SessionId { get; init; }
    public string? Title { get; init; }
    public string? Cwd { get; init; }
    public int Cols { get; init; } = 120;
    public int Rows { get; init; } = 32;
}

public sealed record DirectPtyExitedEventArgs(int? ExitCode, string Reason);

public interface IDirectPtyProcess : IAsyncDisposable
{
    string SessionId { get; }
    int? ProcessId { get; }
    bool HasExited { get; }
    int? ExitCode { get; }
    event EventHandler<byte[]>? OutputReceived;
    event EventHandler<DirectPtyExitedEventArgs>? Exited;
    Task WriteAsync(byte[] bytes, CancellationToken cancellationToken = default);
    void Resize(int cols, int rows);
    Task TerminateAsync(string mode, CancellationToken cancellationToken = default);
}

public interface IDirectPtyBackend
{
    Task<IDirectPtyProcess> SpawnAsync(DirectPtyStartInfo startInfo, CancellationToken cancellationToken = default);
}

/// <summary>
/// Real direct PTY adapter. It is deliberately isolated behind IDirectPtyBackend
/// so app-core tests can use a fake backend and CI never depends on a native PTY.
/// </summary>
public sealed class PortaDirectPtyBackend : IDirectPtyBackend
{
    public async Task<IDirectPtyProcess> SpawnAsync(DirectPtyStartInfo startInfo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        var shell = ResolveShell();
        var options = new PtyOptions
        {
            Name = string.IsNullOrWhiteSpace(startInfo.Title) ? startInfo.SessionId : startInfo.Title,
            Cols = Math.Clamp(startInfo.Cols, 1, 500),
            Rows = Math.Clamp(startInfo.Rows, 1, 500),
            Cwd = string.IsNullOrWhiteSpace(startInfo.Cwd) ? Environment.CurrentDirectory : startInfo.Cwd,
            App = shell,
            Environment = new Dictionary<string, string>
            {
                ["TERM"] = "xterm-256color",
            },
        };

        var connection = await PtyProvider.SpawnAsync(options, cancellationToken).ConfigureAwait(false);
        return new PortaDirectPtyProcess(startInfo.SessionId, connection, cancellationToken);
    }

    private static string ResolveShell()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        }

        var shell = Environment.GetEnvironmentVariable("SHELL");
        if (!string.IsNullOrWhiteSpace(shell) && File.Exists(shell))
        {
            return shell;
        }

        if (File.Exists("/bin/bash"))
        {
            return "/bin/bash";
        }

        return "/bin/sh";
    }
}

internal sealed class PortaDirectPtyProcess : IDirectPtyProcess
{
    private readonly IPtyConnection _connection;
    private readonly CancellationTokenSource _readCancellation;
    private readonly Task _readTask;
    private bool _disposed;

    public PortaDirectPtyProcess(string sessionId, IPtyConnection connection, CancellationToken startupCancellation)
    {
        SessionId = sessionId;
        _connection = connection;
        _readCancellation = CancellationTokenSource.CreateLinkedTokenSource(startupCancellation);
        ProcessId = SafePid(connection);
        connection.ProcessExited += (_, e) =>
        {
            HasExited = true;
            ExitCode = e.ExitCode;
            Exited?.Invoke(this, new DirectPtyExitedEventArgs(e.ExitCode, "process_exited"));
        };
        _readTask = Task.Run(ReadLoopAsync);
    }

    public string SessionId { get; }
    public int? ProcessId { get; }
    public bool HasExited { get; private set; }
    public int? ExitCode { get; private set; }
    public event EventHandler<byte[]>? OutputReceived;
    public event EventHandler<DirectPtyExitedEventArgs>? Exited;

    public async Task WriteAsync(byte[] bytes, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        await _connection.WriterStream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
        await _connection.WriterStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Resize(int cols, int rows)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _connection.Resize(Math.Clamp(cols, 1, 500), Math.Clamp(rows, 1, 500));
    }

    public async Task TerminateAsync(string mode, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.Equals(mode, "interrupt", StringComparison.OrdinalIgnoreCase))
        {
            await WriteAsync([0x03], cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(mode, "graceful", StringComparison.OrdinalIgnoreCase))
        {
            await WriteAsync("exit\r"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            return;
        }

        _connection.Kill();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _readCancellation.Cancel();
        try
        {
            if (!HasExited)
            {
                _connection.Kill();
            }
        }
        catch
        {
            // Best-effort cleanup.
        }

        try
        {
            await _readTask.ConfigureAwait(false);
        }
        catch
        {
            // Read loop shutdown is best-effort.
        }

        _readCancellation.Dispose();
        _connection.Dispose();
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[8192];
        try
        {
            while (!_readCancellation.IsCancellationRequested)
            {
                var read = await _connection.ReaderStream.ReadAsync(buffer, 0, buffer.Length, _readCancellation.Token).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                var copy = new byte[read];
                Buffer.BlockCopy(buffer, 0, copy, 0, read);
                OutputReceived?.Invoke(this, copy);
            }
        }
        catch (OperationCanceledException) when (_readCancellation.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            Exited?.Invoke(this, new DirectPtyExitedEventArgs(ExitCode, $"pty_read_failed: {ex.Message}"));
        }
    }

    private static int? SafePid(IPtyConnection connection)
    {
        try
        {
            return connection.Pid;
        }
        catch
        {
            return null;
        }
    }
}
