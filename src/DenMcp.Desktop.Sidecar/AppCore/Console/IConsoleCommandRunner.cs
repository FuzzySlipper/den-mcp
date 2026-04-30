namespace DenMcp.Desktop.Sidecar;

/// <summary>
/// Interface for the console command registry and runner.
/// All safe built-in actions are registered here; React only calls through typed bridge methods.
/// </summary>
public interface IConsoleCommandRunner
{
    /// <summary>List all registered safe built-in commands.</summary>
    IReadOnlyList<ConsoleCommandDefinition> ListCommands();

    /// <summary>Run a registered command and return structured output.</summary>
    Task<ConsoleCommandRunResponse> RunCommandAsync(ConsoleCommandRunRequest request, CancellationToken cancellationToken = default);
}
