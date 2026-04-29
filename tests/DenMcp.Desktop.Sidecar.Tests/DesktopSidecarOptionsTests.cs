using DenMcp.Desktop.Sidecar;

namespace DenMcp.Desktop.Sidecar.Tests;

public class DesktopSidecarOptionsTests
{
    [Fact]
    public void Parse_UsesArgsAndKeepsSecretOutOfProcessArgumentEcho()
    {
        var options = DesktopSidecarOptions.Parse(
            new[]
            {
                "--app-id", "den-desktop-dev",
                "--app-version=0.2.0",
                "--config-path", "./.den-desktop-dev",
                "--log-path", "./.den-desktop-logs",
                "--auth-token", "secret-token",
                "--port", "51990",
                "--endpoint-path", "bridge",
            },
            new Dictionary<string, string?>());

        Assert.Equal("den-desktop-dev", options.AppId);
        Assert.Equal("0.2.0", options.AppVersion);
        Assert.EndsWith(".den-desktop-dev", options.ConfigPath, StringComparison.Ordinal);
        Assert.EndsWith(".den-desktop-logs", options.LogPath, StringComparison.Ordinal);
        Assert.Equal("secret-token", options.AuthToken);
        Assert.Equal(51990, options.Port);
        Assert.Equal("/bridge", options.EndpointPath);
        Assert.DoesNotContain("secret-token", options.ToSidecarArgumentsWithoutSecret());
    }

    [Fact]
    public void Parse_UsesEnvironmentFallbacks()
    {
        var options = DesktopSidecarOptions.Parse(
            Array.Empty<string>(),
            new Dictionary<string, string?>
            {
                ["DEN_DESKTOP_BRIDGE_TOKEN"] = "env-token",
                ["DEN_DESKTOP_CONFIG_PATH"] = "/tmp/den-desktop/config",
                ["DEN_DESKTOP_BRIDGE_PORT"] = "0",
            });

        Assert.Equal(DesktopSidecarOptions.DefaultAppId, options.AppId);
        Assert.Equal("env-token", options.AuthToken);
        Assert.Equal("/tmp/den-desktop/config", options.ConfigPath);
        Assert.Equal(0, options.Port);
    }

    [Fact]
    public void Parse_RequiresTokenExceptForContractPrinting()
    {
        Assert.Throws<ArgumentException>(() => DesktopSidecarOptions.Parse(Array.Empty<string>(), new Dictionary<string, string?>()));

        var printSchema = DesktopSidecarOptions.Parse(new[] { "--print-schema" }, new Dictionary<string, string?>());
        Assert.True(printSchema.PrintSchema);
    }
}
