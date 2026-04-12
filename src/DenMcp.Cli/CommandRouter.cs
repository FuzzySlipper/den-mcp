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
        var positionals = GetPositionals();
        return index < positionals.Count ? positionals[index] : null;
    }

    public IReadOnlyList<string> GetPositionals(params string[] booleanFlags)
    {
        var boolSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var flag in booleanFlags)
        {
            boolSet.Add($"--{flag}");
            boolSet.Add($"-{flag[0]}");
        }

        var positionals = new List<string>();
        for (var i = 1; i < _args.Length; i++)
        {
            var arg = _args[i];
            if (!arg.StartsWith('-'))
            {
                positionals.Add(arg);
                continue;
            }

            if (boolSet.Contains(arg))
                continue;

            if (i + 1 < _args.Length && !_args[i + 1].StartsWith('-'))
                i++;
        }

        return positionals;
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
