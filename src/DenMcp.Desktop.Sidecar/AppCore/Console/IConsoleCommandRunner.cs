namespace DenMcp.Desktop.Sidecar;

/// <summary>
/// Callback for reporting structured console command output lines as progress events.
/// Receives each line produced during execution so the bridge handler can forward them
/// as progress frames to the caller.
/// </summary>
public delegate ValueTask ConsoleCommandProgressCallback(ConsoleCommandLine line, CancellationToken cancellationToken);

/// <summary>
/// Interface for the console command registry and runner.
/// All safe built-in actions are registered here; React only calls through typed bridge methods.
/// </summary>
public interface IConsoleCommandRunner
{
    /// <summary>List all registered safe built-in commands.</summary>
    IReadOnlyList<ConsoleCommandDefinition> ListCommands();

    /// <summary>Run a registered command and return structured output.
    /// When <paramref name="onProgress"/> is provided, the runner emits each structured
    /// line as a progress event during execution.</summary>
    Task<ConsoleCommandRunResponse> RunCommandAsync(ConsoleCommandRunRequest request, ConsoleCommandProgressCallback? onProgress = null, CancellationToken cancellationToken = default);
}
