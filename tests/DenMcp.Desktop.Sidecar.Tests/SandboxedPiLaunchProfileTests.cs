using System.Text.Json;

namespace DenMcp.Desktop.Sidecar.Tests;

public class SandboxedPiLaunchProfileBuilderTests
{
    private const string TestComposeFile = "/home/user/pi-docker/compose.yaml";
    private const string TestDevDir = "/home/user/dev";

    // ── Happy path: default profile builds with dedicated_per_run ────────

    [Fact]
    public void Build_DefaultProfile_UsesDedicatedPerRun()
    {
        var profile = ValidBuilder().Build();

        Assert.Equal(PiConfigStrategies.DedicatedPerRun, profile.PiConfigStrategy);
        Assert.Equal(SandboxKinds.DockerComposePi, profile.SandboxKind);
        Assert.Equal(SandboxDefaults.Service, profile.Service);
        Assert.Equal(PiLaunchModes.InteractiveCli, profile.PiLaunchMode);
        Assert.Equal(ToolProfiles.Coding, profile.ToolProfile);
        Assert.Equal(NetworkProfiles.Unrestricted, profile.NetworkProfile);
        Assert.Null(profile.DebtMetadata);
    }

    [Fact]
    public void Build_DerivesProfileIdFromProjectAndTask()
    {
        var profile = ValidBuilder()
            .WithTaskId(910)
            .Build();

        Assert.StartsWith("sandbox-pi:den-mcp:", profile.ProfileId);
        Assert.Equal("den-mcp", profile.ProjectId);
        Assert.Equal(910, profile.TaskId);
    }

    [Fact]
    public void Build_DerivesContainerWorkdirFromProject()
    {
        var profile = ValidBuilder().Build();

        Assert.Equal("/home/pi/dev/den-mcp", profile.ContainerWorkdir);
    }

    [Fact]
    public void Build_PreservesExplicitContainerWorkdir()
    {
        var profile = ValidBuilder()
            .WithContainerWorkdir("/home/pi/dev/custom")
            .Build();

        Assert.Equal("/home/pi/dev/custom", profile.ContainerWorkdir);
    }

    // ── R910-1: Pi config strategy validation ────────────────────────────

    [Fact]
    public void Build_DedicatedPerRun_SucceedsByDefault()
    {
        var profile = ValidBuilder()
            .WithPiConfigStrategy(PiConfigStrategies.DedicatedPerRun)
            .Build();

        Assert.Equal(PiConfigStrategies.DedicatedPerRun, profile.PiConfigStrategy);
        Assert.Null(profile.DebtMetadata);
    }

    [Fact]
    public void Build_DedicatedPerRun_WithExplicitDir_Succeeds()
    {
        var profile = ValidBuilder()
            .WithPiConfigStrategy(PiConfigStrategies.DedicatedPerRun)
            .WithPiConfigDir("/tmp/den-sandbox-pi/run-abc")
            .Build();

        Assert.Equal(PiConfigStrategies.DedicatedPerRun, profile.PiConfigStrategy);
        Assert.Equal("/tmp/den-sandbox-pi/run-abc", profile.PiConfigDir);
    }

    [Fact]
    public void Build_HostBindRw_RequiresCapabilityWarnings()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ValidBuilder()
                .WithPiConfigStrategy(PiConfigStrategies.HostBindRw)
                .WithDebtMetadata(new SandboxedPiLaunchDebtMetadata
                {
                    Reason = "testing",
                    TrackingTaskId = 999,
                })
                .Build());

        Assert.Contains("capability_warning", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_HostBindRw_RequiresDebtMetadata()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ValidBuilder()
                .WithPiConfigStrategy(PiConfigStrategies.HostBindRw)
                .WithCapabilityWarnings(["Host ~/.pi exposed read-write to sandbox"])
                .Build());

        Assert.Contains("debt_metadata", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_HostBindRw_SucceedsWithWarningsAndDebt()
    {
        var profile = ValidBuilder()
            .WithPiConfigStrategy(PiConfigStrategies.HostBindRw)
            .WithCapabilityWarnings(["Host ~/.pi exposed read-write to sandbox"])
            .WithDebtMetadata(new SandboxedPiLaunchDebtMetadata
            {
                Reason = "Per-run auth not yet implemented",
                TrackingTaskId = 1073,
                AcceptedAt = "2026-05-01T00:00:00Z",
            })
            .Build();

        Assert.Equal(PiConfigStrategies.HostBindRw, profile.PiConfigStrategy);
        Assert.NotNull(profile.DebtMetadata);
        Assert.Equal(1073, profile.DebtMetadata!.TrackingTaskId);
        Assert.Contains("Host ~/.pi exposed read-write to sandbox", profile.CapabilityWarnings, StringComparer.Ordinal);
    }

    [Fact]
    public void Build_InvalidPiConfigStrategy_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ValidBuilder()
                .WithPiConfigStrategy("arbitrary_strategy")
                .Build());

        Assert.Contains("pi_config_strategy", ex.Message, StringComparison.Ordinal);
    }

    // ── R910-2: OAuth port validation ────────────────────────────────────

    [Fact]
    public void Build_AllowListedPorts_Succeeds()
    {
        var profile = ValidBuilder()
            .WithOAuthPortConfig(new SandboxedPiOAuthPortConfig
            {
                Strategy = OAuthPortStrategies.AllowListed,
                AllowedPorts = [3000, 3001],
                BindAddress = SandboxDefaults.LoopbackAddress,
            })
            .Build();

        Assert.Equal(OAuthPortStrategies.AllowListed, profile.OAuthPortConfig.Strategy);
        Assert.Equal(2, profile.OAuthPortConfig.AllowedPorts.Count);
        Assert.Equal(SandboxDefaults.LoopbackAddress, profile.OAuthPortConfig.BindAddress);
    }

    [Fact]
    public void Build_AllowListedNoPorts_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ValidBuilder()
                .WithOAuthPortConfig(new SandboxedPiOAuthPortConfig
                {
                    Strategy = OAuthPortStrategies.AllowListed,
                    AllowedPorts = [],
                    BindAddress = SandboxDefaults.LoopbackAddress,
                })
                .Build());

        Assert.Contains("at least one allowed_port", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_NonLoopbackBindAddress_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ValidBuilder()
                .WithOAuthPortConfig(new SandboxedPiOAuthPortConfig
                {
                    Strategy = OAuthPortStrategies.AllowListed,
                    AllowedPorts = [3000],
                    BindAddress = "0.0.0.0",
                })
                .Build());

        Assert.Contains("127.0.0.1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("loopback", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_InvalidPort_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ValidBuilder()
                .WithOAuthPortConfig(new SandboxedPiOAuthPortConfig
                {
                    Strategy = OAuthPortStrategies.AllowListed,
                    AllowedPorts = [3000, 99999],
                    BindAddress = SandboxDefaults.LoopbackAddress,
                })
                .Build());

        Assert.Contains("invalid port 99999", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_ManualFallback_Succeeds()
    {
        var profile = ValidBuilder()
            .WithOAuthPortConfig(new SandboxedPiOAuthPortConfig
            {
                Strategy = OAuthPortStrategies.ManualFallback,
                ManualFallbackInstructions = "Run `pi login` manually inside the container.",
            })
            .Build();

        Assert.Equal(OAuthPortStrategies.ManualFallback, profile.OAuthPortConfig.Strategy);
        Assert.NotNull(profile.OAuthPortConfig.ManualFallbackInstructions);
    }

    [Fact]
    public void Build_Disabled_Succeeds()
    {
        var profile = ValidBuilder()
            .WithOAuthPortConfig(new SandboxedPiOAuthPortConfig
            {
                Strategy = OAuthPortStrategies.Disabled,
            })
            .Build();

        Assert.Equal(OAuthPortStrategies.Disabled, profile.OAuthPortConfig.Strategy);
    }

    [Fact]
    public void Build_InvalidOAuthStrategy_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ValidBuilder()
                .WithOAuthPortConfig(new SandboxedPiOAuthPortConfig
                {
                    Strategy = "any_interface",
                })
                .Build());

        Assert.Contains("oauth_port_config.strategy", ex.Message, StringComparison.Ordinal);
    }

    // ── Allow-list validation ────────────────────────────────────────────

    [Fact]
    public void Build_ComposeFileNotAllowListed_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ValidBuilder(allowedComposeFiles: ["/opt/pi-docker/compose.yaml"])
                .WithComposeFile("/etc/evil/compose.yaml")
                .Build());

        Assert.Contains("not in the allow-listed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_ComposeFileAllowListed_Succeeds()
    {
        var profile = ValidBuilder(allowedComposeFiles: [TestComposeFile])
            .Build();

        Assert.Equal(TestComposeFile, profile.ComposeFile);
    }

    [Fact]
    public void Build_NoAllowList_AcceptsAnyComposeFile()
    {
        var profile = ValidBuilder(allowedComposeFiles: null)
            .Build();

        Assert.Equal(TestComposeFile, profile.ComposeFile);
    }

    [Fact]
    public void Build_InvalidService_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ValidBuilder()
                .WithService("malicious")
                .Build());

        Assert.Contains("not allow-listed", ex.Message, StringComparison.Ordinal);
    }

    // ── Required field validation ────────────────────────────────────────

    [Fact]
    public void Build_MissingProjectId_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new SandboxedPiLaunchProfileBuilder()
                .WithComposeFile(TestComposeFile)
                .WithDevDir(TestDevDir)
                .Build());

        Assert.Contains("project_id", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_MissingComposeFile_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new SandboxedPiLaunchProfileBuilder()
                .WithProjectId("den-mcp")
                .WithDevDir(TestDevDir)
                .Build());

        Assert.Contains("compose_file", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_MissingDevDir_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new SandboxedPiLaunchProfileBuilder()
                .WithProjectId("den-mcp")
                .WithComposeFile(TestComposeFile)
                .Build());

        Assert.Contains("dev_dir", ex.Message, StringComparison.Ordinal);
    }

    // ── Credential mount validation ──────────────────────────────────────

    [Fact]
    public void Build_ValidCredentialMounts_Succeeds()
    {
        var profile = ValidBuilder()
            .WithCredentialMounts([CredentialMountKinds.GitConfig, CredentialMountKinds.Ssh])
            .Build();

        Assert.Equal(2, profile.CredentialMounts.Count);
    }

    [Fact]
    public void Build_InvalidCredentialMount_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ValidBuilder()
                .WithCredentialMounts([CredentialMountKinds.GitConfig, "docker_socket"])
                .Build());

        Assert.Contains("docker_socket", ex.Message, StringComparison.Ordinal);
        Assert.Contains("not allow-listed", ex.Message, StringComparison.Ordinal);
    }

    // ── Tool profile validation ──────────────────────────────────────────

    [Theory]
    [InlineData(ToolProfiles.Coding)]
    [InlineData(ToolProfiles.ReadOnly)]
    [InlineData(ToolProfiles.NoTools)]
    public void Build_ValidToolProfile_Succeeds(string toolProfile)
    {
        var profile = ValidBuilder()
            .WithToolProfile(toolProfile)
            .Build();

        Assert.Equal(toolProfile, profile.ToolProfile);
    }

    [Fact]
    public void Build_InvalidToolProfile_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ValidBuilder()
                .WithToolProfile("rm_rf")
                .Build());

        Assert.Contains("tool_profile", ex.Message, StringComparison.Ordinal);
    }

    // ── Network profile validation ───────────────────────────────────────

    [Fact]
    public void Build_UnrestrictedNetwork_AddsWarning()
    {
        var profile = ValidBuilder().Build();

        Assert.Contains(profile.CapabilityWarnings, w => w.Contains("unrestricted", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_InvalidNetworkProfile_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ValidBuilder()
                .WithNetworkProfile("root_access")
                .Build());

        Assert.Contains("network_profile", ex.Message, StringComparison.Ordinal);
    }

    // ── Serialization roundtrip ──────────────────────────────────────────

    [Fact]
    public void Profile_SerializesAndDeserializes()
    {
        var profile = ValidBuilder()
            .WithTaskId(910)
            .WithPiConfigDir("/tmp/den-sandbox-pi/run-test")
            .Build();

        var json = JsonSerializer.Serialize(profile);
        var deserialized = JsonSerializer.Deserialize<SandboxedPiLaunchProfile>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(profile.ProfileId, deserialized!.ProfileId);
        Assert.Equal(profile.ProjectId, deserialized.ProjectId);
        Assert.Equal(profile.PiConfigStrategy, deserialized.PiConfigStrategy);
        Assert.Equal(profile.PiConfigDir, deserialized.PiConfigDir);
        Assert.Equal(profile.OAuthPortConfig.Strategy, deserialized.OAuthPortConfig.Strategy);
        Assert.Equal(profile.OAuthPortConfig.BindAddress, deserialized.OAuthPortConfig.BindAddress);
    }

    // ── Helper ───────────────────────────────────────────────────────────

    private static SandboxedPiLaunchProfileBuilder ValidBuilder(
        IEnumerable<string>? allowedComposeFiles = null)
    {
        return new SandboxedPiLaunchProfileBuilder(allowedComposeFiles)
            .WithProjectId("den-mcp")
            .WithComposeFile(TestComposeFile)
            .WithDevDir(TestDevDir);
    }
}

public class SandboxedPiCommandBuilderTests
{
    private const string TestComposeFile = "/home/user/pi-docker/compose.yaml";

    private static SandboxedPiLaunchProfile ValidProfile() =>
        new SandboxedPiLaunchProfileBuilder()
            .WithProjectId("den-mcp")
            .WithComposeFile(TestComposeFile)
            .WithDevDir("/home/user/dev")
            .WithContainerWorkdir("/home/pi/dev/den-mcp")
            .Build();

    // ── Compose up args ──────────────────────────────────────────────────

    [Fact]
    public void BuildComposeUpArgs_ContainsFileAndService()
    {
        var builder = new SandboxedPiCommandBuilder();
        var profile = ValidProfile();
        var args = builder.BuildComposeUpArgs(profile);

        Assert.Contains("compose", args);
        Assert.Contains("-f", args);
        Assert.Contains(TestComposeFile, args);
        Assert.Contains("up", args);
        Assert.Contains("-d", args);
        Assert.Contains(SandboxDefaults.Service, args);
    }

    // ── Tmux exec args ───────────────────────────────────────────────────

    [Fact]
    public void BuildTmuxExecArgs_UsesArgumentVectorNotShellString()
    {
        var builder = new SandboxedPiCommandBuilder();
        var profile = ValidProfile();
        var args = builder.BuildTmuxExecArgs(profile, "den-pi-test-session");

        // Must contain docker compose exec
        Assert.Contains("compose", args);
        Assert.Contains("-f", args);
        Assert.Contains("exec", args);
        Assert.Contains("-T", args);

        // Must contain tmux session creation
        Assert.Contains("tmux", args);
        Assert.Contains("new-session", args);
        Assert.Contains("-d", args);
        Assert.Contains("-s", args);
        Assert.Contains("den-pi-test-session", args);

        // Must contain pi command (not a shell string)
        Assert.Contains("pi", args);

        // Default tool profile is coding -> --tools read,bash,edit,write
        var toolsIndex = ((List<string>)args).IndexOf("--tools");
        Assert.True(toolsIndex >= 0, "Expected --tools argument");
        Assert.True(toolsIndex + 1 < args.Count, "Expected tools value after --tools");
        Assert.Equal("read,bash,edit,write", args[toolsIndex + 1]);
    }

    [Fact]
    public void BuildTmuxExecArgs_ReadOnlyProfile_UsesReadOnlyTools()
    {
        var builder = new SandboxedPiCommandBuilder();
        var profile = new SandboxedPiLaunchProfileBuilder()
            .WithProjectId("den-mcp")
            .WithComposeFile(TestComposeFile)
            .WithDevDir("/home/user/dev")
            .WithToolProfile(ToolProfiles.ReadOnly)
            .Build();

        var args = builder.BuildTmuxExecArgs(profile, "den-pi-readonly");

        var toolsIndex = ((List<string>)args).IndexOf("--tools");
        Assert.True(toolsIndex >= 0);
        Assert.Equal("read,bash", args[toolsIndex + 1]);
    }

    [Fact]
    public void BuildTmuxExecArgs_NoToolsProfile_UsesNoToolsFlag()
    {
        var builder = new SandboxedPiCommandBuilder();
        var profile = new SandboxedPiLaunchProfileBuilder()
            .WithProjectId("den-mcp")
            .WithComposeFile(TestComposeFile)
            .WithDevDir("/home/user/dev")
            .WithToolProfile(ToolProfiles.NoTools)
            .Build();

        var args = builder.BuildTmuxExecArgs(profile, "den-pi-notools");

        Assert.Contains("--no-tools", args);
        Assert.DoesNotContain("--tools", args);
    }

    [Fact]
    public void BuildTmuxExecArgs_ModelAndProviderPassed()
    {
        var builder = new SandboxedPiCommandBuilder();
        var profile = new SandboxedPiLaunchProfileBuilder()
            .WithProjectId("den-mcp")
            .WithComposeFile(TestComposeFile)
            .WithDevDir("/home/user/dev")
            .WithModel("gpt-4")
            .WithProvider("openai")
            .Build();

        var args = builder.BuildTmuxExecArgs(profile, "den-pi-model");

        Assert.Contains("--model", args);
        Assert.Contains("gpt-4", args);
        Assert.Contains("--provider", args);
        Assert.Contains("openai", args);
    }

    // ── OAuth port args ──────────────────────────────────────────────────

    [Fact]
    public void BuildOAuthPortArgs_AllowListed_ProducesLoopbackPublish()
    {
        var builder = new SandboxedPiCommandBuilder();
        var profile = new SandboxedPiLaunchProfileBuilder()
            .WithProjectId("den-mcp")
            .WithComposeFile(TestComposeFile)
            .WithDevDir("/home/user/dev")
            .WithOAuthPortConfig(new SandboxedPiOAuthPortConfig
            {
                Strategy = OAuthPortStrategies.AllowListed,
                AllowedPorts = [3000, 3001],
                BindAddress = SandboxDefaults.LoopbackAddress,
            })
            .Build();

        var args = builder.BuildOAuthPortArgs(profile);

        // Should have --publish 127.0.0.1:3000:3000 and --publish 127.0.0.1:3001:3001
        Assert.Contains("--publish", args);
        Assert.Contains("127.0.0.1:3000:3000", args);
        Assert.Contains("127.0.0.1:3001:3001", args);
    }

    [Fact]
    public void BuildOAuthPortArgs_Disabled_ReturnsEmpty()
    {
        var builder = new SandboxedPiCommandBuilder();
        var profile = new SandboxedPiLaunchProfileBuilder()
            .WithProjectId("den-mcp")
            .WithComposeFile(TestComposeFile)
            .WithDevDir("/home/user/dev")
            .WithOAuthPortConfig(new SandboxedPiOAuthPortConfig
            {
                Strategy = OAuthPortStrategies.Disabled,
            })
            .Build();

        var args = builder.BuildOAuthPortArgs(profile);

        Assert.Empty(args);
    }

    [Fact]
    public void BuildOAuthPortArgs_ManualFallback_ReturnsEmpty()
    {
        var builder = new SandboxedPiCommandBuilder();
        var profile = new SandboxedPiLaunchProfileBuilder()
            .WithProjectId("den-mcp")
            .WithComposeFile(TestComposeFile)
            .WithDevDir("/home/user/dev")
            .WithOAuthPortConfig(new SandboxedPiOAuthPortConfig
            {
                Strategy = OAuthPortStrategies.ManualFallback,
            })
            .Build();

        var args = builder.BuildOAuthPortArgs(profile);

        Assert.Empty(args);
    }

    // ── Pi config volume args ────────────────────────────────────────────

    [Fact]
    public void BuildPiConfigVolumeArgs_DedicatedPerRun_WithDir_UsesExplicitDir()
    {
        var builder = new SandboxedPiCommandBuilder();
        var profile = new SandboxedPiLaunchProfileBuilder()
            .WithProjectId("den-mcp")
            .WithComposeFile(TestComposeFile)
            .WithDevDir("/home/user/dev")
            .WithPiConfigStrategy(PiConfigStrategies.DedicatedPerRun)
            .WithPiConfigDir("/tmp/den-sandbox-pi/run-abc")
            .Build();

        var args = builder.BuildPiConfigVolumeArgs(profile);

        Assert.Equal(["-v", "/tmp/den-sandbox-pi/run-abc:/home/pi/.pi"], args);
    }

    [Fact]
    public void BuildPiConfigVolumeArgs_DedicatedPerRun_WithoutDir_ReturnsEmpty()
    {
        var builder = new SandboxedPiCommandBuilder();
        var profile = new SandboxedPiLaunchProfileBuilder()
            .WithProjectId("den-mcp")
            .WithComposeFile(TestComposeFile)
            .WithDevDir("/home/user/dev")
            .WithPiConfigStrategy(PiConfigStrategies.DedicatedPerRun)
            .Build();

        var args = builder.BuildPiConfigVolumeArgs(profile);

        Assert.Empty(args);
    }

    // ── Tmux session name ────────────────────────────────────────────────

    [Fact]
    public void BuildTmuxSessionName_IsDeterministicAndBounded()
    {
        var profile = ValidProfile();
        var name = SandboxedPiCommandBuilder.BuildTmuxSessionName(profile, "unique");

        Assert.StartsWith("den-pi-den-mcp", name);
        Assert.True(name.Length <= 80, $"Session name too long: {name.Length}");
    }

    [Fact]
    public void BuildTmuxSessionName_WithTask_IncludesTask()
    {
        var profile = new SandboxedPiLaunchProfileBuilder()
            .WithProjectId("den-mcp")
            .WithComposeFile(TestComposeFile)
            .WithDevDir("/home/user/dev")
            .WithTaskId(910)
            .Build();

        var name = SandboxedPiCommandBuilder.BuildTmuxSessionName(profile);

        Assert.Contains("task910", name);
    }

    // ── Session constraints JSON ─────────────────────────────────────────

    [Fact]
    public void BuildSessionConstraints_IncludesKeyFields_NoRawTerminal()
    {
        var profile = ValidProfile();
        var json = SandboxedPiCommandBuilder.BuildSessionConstraints(profile);

        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(SandboxKinds.DockerComposePi, root.GetProperty("sandbox_kind").GetString());
        Assert.Equal(PiConfigStrategies.DedicatedPerRun, root.GetProperty("pi_config_strategy").GetString());
        Assert.Equal(OAuthPortStrategies.AllowListed, root.GetProperty("oauth_port_strategy").GetString());
        Assert.Equal(ToolProfiles.Coding, root.GetProperty("tool_profile").GetString());
        Assert.Equal("local_only", root.GetProperty("raw_stream_scope").GetString());

        // No Docker-specific host paths
        Assert.False(root.TryGetProperty("container_id", out _));
        Assert.False(root.TryGetProperty("docker_socket", out _));
    }

    [Fact]
    public void BuildSessionConstraints_IncludesWarnings_WhenPresent()
    {
        var profile = new SandboxedPiLaunchProfileBuilder()
            .WithProjectId("den-mcp")
            .WithComposeFile(TestComposeFile)
            .WithDevDir("/home/user/dev")
            .Build();

        var json = SandboxedPiCommandBuilder.BuildSessionConstraints(profile);
        var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("capability_warnings", out var warnings));
        Assert.True(warnings.GetArrayLength() > 0);
    }
}

public class ToolProfileTests
{
    [Theory]
    [InlineData(ToolProfiles.Coding, "read,bash,edit,write")]
    [InlineData(ToolProfiles.ReadOnly, "read,bash")]
    [InlineData(ToolProfiles.NoTools, null)]
    public void ToPiArgs_ProducesCorrectArgs(string profile, string? expectedToolsValue)
    {
        var args = ToolProfiles.ToPiArgs(profile);

        if (expectedToolsValue is not null)
        {
            Assert.Equal(["--tools", expectedToolsValue], args);
        }
        else
        {
            Assert.Equal(["--no-tools"], args);
        }
    }

    [Fact]
    public void ValidProfiles_ContainsExpectedSet()
    {
        Assert.Equal(3, ToolProfiles.ValidProfiles.Count);
        Assert.Contains(ToolProfiles.Coding, ToolProfiles.ValidProfiles);
        Assert.Contains(ToolProfiles.ReadOnly, ToolProfiles.ValidProfiles);
        Assert.Contains(ToolProfiles.NoTools, ToolProfiles.ValidProfiles);
    }
}

public class CredentialMountTests
{
    [Fact]
    public void ValidKinds_ContainsExpectedSet()
    {
        Assert.Equal(3, CredentialMountKinds.ValidKinds.Count);
        Assert.Contains(CredentialMountKinds.GitConfig, CredentialMountKinds.ValidKinds);
        Assert.Contains(CredentialMountKinds.Ssh, CredentialMountKinds.ValidKinds);
        Assert.Contains(CredentialMountKinds.Gh, CredentialMountKinds.ValidKinds);
    }
}
