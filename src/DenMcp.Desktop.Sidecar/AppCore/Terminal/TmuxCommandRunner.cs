using System.Diagnostics;
using System.Text;

namespace DenMcp.Desktop.Sidecar;

public interface ITmuxCommandRunner
{
    Task<TmuxCommandResult> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default);
}

public sealed record TmuxCommandResult
{
    public required int ExitCode { get; init; }
    public string Stdout { get; init; } = string.Empty;
    public string Stderr { get; init; } = string.Empty;

    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Bounded tmux process runner. The executable and argv are supplied separately
/// and never through a shell command string.
/// </summary>
public sealed class SystemTmuxCommandRunner : ITmuxCommandRunner
{
    private readonly string _executable;
    private readonly TimeSpan _timeout;

    public SystemTmuxCommandRunner(string executable = "tmux", TimeSpan? timeout = null)
    {
        _executable = string.IsNullOrWhiteSpace(executable) ? "tmux" : executable;
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    public async Task<TmuxCommandResult> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);

        var startInfo = new ProcessStartInfo
        {
            FileName = _executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        try
        {
            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

            if (!process.Start())
            {
                return new TmuxCommandResult { ExitCode = 127, Stderr = "Unable to start tmux process." };
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            return new TmuxCommandResult
            {
                ExitCode = process.ExitCode,
                Stdout = stdout.ToString(),
                Stderr = stderr.ToString(),
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new TmuxCommandResult { ExitCode = 124, Stderr = "tmux command timed out." };
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new TmuxCommandResult { ExitCode = 127, Stderr = ex.Message };
        }
    }
}
