namespace DenMcp.Cli;

public sealed class CommandRouter
{
    private readonly string[] _args;

    public CommandRouter(string[] args)
    {
        _args = args;
    }

    public string? Command => _args.Length > 0 ? _args[0] : null;

    public string? GetPositional(int index)
    {
        // Positional args after the command, skipping flags
        var positionals = _args.Skip(1).Where(a => !a.StartsWith('-')).ToList();
        return index < positionals.Count ? positionals[index] : null;
    }

    public string? GetFlag(string name)
    {
        for (var i = 0; i < _args.Length - 1; i++)
        {
            if (_args[i] == $"--{name}" || _args[i] == $"-{name[0]}")
                return _args[i + 1];
        }
        return null;
    }

    public bool HasFlag(string name) =>
        _args.Any(a => a == $"--{name}" || a == $"-{name[0]}");

    public string? Project => GetFlag("project") ?? GetFlag("p") ?? DetectProject();

    private static string? DetectProject()
    {
        var dir = Directory.GetCurrentDirectory();
        return Path.GetFileName(dir);
    }
}
