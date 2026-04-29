using System.Reflection;

namespace DenMcp.Desktop.Sidecar;

public sealed record DesktopSidecarOptions
{
    public const string DefaultAppId = "den-desktop";
    public const string DefaultEndpointPath = "/bridge";
    public const int DefaultPort = 0;

    public required string AppId { get; init; }

    public required string AppVersion { get; init; }

    public required string ConfigPath { get; init; }

    public string? LogPath { get; init; }

    public required string AuthToken { get; init; }

    public int Port { get; init; }

    public required string EndpointPath { get; init; }

    public bool PrintSchema { get; init; }

    public bool PrintWireFixture { get; init; }

    public static DesktopSidecarOptions Parse(
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        environment ??= Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => entry.Value?.ToString(), StringComparer.OrdinalIgnoreCase);

        var values = ParseArgs(args);
        var options = new DesktopSidecarOptions
        {
            AppId = Value(values, environment, "app-id", "DEN_DESKTOP_APP_ID") ?? DefaultAppId,
            AppVersion = Value(values, environment, "app-version", "DEN_DESKTOP_APP_VERSION") ?? DefaultVersion(),
            ConfigPath = ExpandHome(Value(values, environment, "config-path", "DEN_DESKTOP_CONFIG_PATH") ?? "~/.den-mcp/desktop"),
            LogPath = OptionalExpanded(Value(values, environment, "log-path", "DEN_DESKTOP_LOG_PATH")),
            AuthToken = Value(values, environment, "auth-token", "DEN_DESKTOP_BRIDGE_TOKEN") ?? string.Empty,
            Port = ParsePort(Value(values, environment, "port", "DEN_DESKTOP_BRIDGE_PORT")),
            EndpointPath = NormalizePath(Value(values, environment, "endpoint-path", "DEN_DESKTOP_BRIDGE_PATH") ?? DefaultEndpointPath),
            PrintSchema = values.ContainsKey("print-schema"),
            PrintWireFixture = values.ContainsKey("print-wire-fixture"),
        };

        options.Validate();
        return options;
    }

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(AppId);
        ArgumentException.ThrowIfNullOrWhiteSpace(AppVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(ConfigPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(EndpointPath);

        if (!PrintSchema && !PrintWireFixture)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(AuthToken);
        }

        if (Port is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(Port), "Bridge port must be between 0 and 65535.");
        }
    }

    public string[] ToSidecarArgumentsWithoutSecret()
    {
        var args = new List<string>
        {
            "--app-id",
            AppId,
            "--app-version",
            AppVersion,
            "--config-path",
            ConfigPath,
            "--port",
            Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--endpoint-path",
            EndpointPath,
        };

        if (!string.IsNullOrWhiteSpace(LogPath))
        {
            args.Add("--log-path");
            args.Add(LogPath);
        }

        return args.ToArray();
    }

    private static Dictionary<string, string?> ParseArgs(IReadOnlyList<string> args)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected sidecar argument '{arg}'. Arguments must use --name value or --name=value.");
            }

            var withoutPrefix = arg[2..];
            var equalsIndex = withoutPrefix.IndexOf('=');
            if (equalsIndex >= 0)
            {
                values[withoutPrefix[..equalsIndex]] = withoutPrefix[(equalsIndex + 1)..];
                continue;
            }

            if (IsSwitch(withoutPrefix))
            {
                values[withoutPrefix] = "true";
                continue;
            }

            if (index + 1 >= args.Count)
            {
                throw new ArgumentException($"Missing value for sidecar argument '{arg}'.");
            }

            values[withoutPrefix] = args[++index];
        }

        return values;
    }

    private static bool IsSwitch(string name)
    {
        return string.Equals(name, "print-schema", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "print-wire-fixture", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Value(
        IReadOnlyDictionary<string, string?> args,
        IReadOnlyDictionary<string, string?> environment,
        string argName,
        string envName)
    {
        return args.TryGetValue(argName, out var argValue) ? argValue : EnvironmentValue(environment, envName);
    }

    private static string? EnvironmentValue(IReadOnlyDictionary<string, string?> environment, string name)
    {
        return environment.TryGetValue(name, out var value) ? value : null;
    }

    private static int ParsePort(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultPort;
        }

        if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port))
        {
            throw new ArgumentException($"Bridge port '{value}' is not a valid integer.");
        }

        return port;
    }

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = path.StartsWith("/", StringComparison.Ordinal) ? path : "/" + path;
        return normalized.TrimEnd('/') is { Length: > 0 } trimmed ? trimmed : DefaultEndpointPath;
    }

    private static string? OptionalExpanded(string? path)
    {
        return string.IsNullOrWhiteSpace(path) ? null : ExpandHome(path);
    }

    private static string ExpandHome(string path)
    {
        if (path == "~")
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        if (path.StartsWith("~/", StringComparison.Ordinal))
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        }

        return Path.GetFullPath(path);
    }

    private static string DefaultVersion()
    {
        return typeof(DesktopSidecarOptions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(DesktopSidecarOptions).Assembly.GetName().Version?.ToString()
            ?? "0.1.0";
    }
}
