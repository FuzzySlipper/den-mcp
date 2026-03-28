using System.Reflection;

namespace Architecture.Tests;

public class DependencyBoundaryTests
{
    private static readonly Assembly CoreAssembly = typeof(DenMcp.Core.Models.Project).Assembly;
    private static readonly Assembly ServerAssembly = typeof(DenMcp.Server.DenMcpOptions).Assembly;
    private static readonly Assembly CliAssembly = typeof(DenMcp.Cli.DenApiClient).Assembly;

    [Fact]
    public void Core_DoesNotReference_AspNetCore()
    {
        var refs = CoreAssembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        Assert.DoesNotContain(refs, name => name.Contains("AspNetCore", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Core_DoesNotReference_TerminalGui()
    {
        var refs = CoreAssembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        Assert.DoesNotContain(refs, name => name.Contains("Terminal.Gui", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Core_DoesNotReference_ModelContextProtocol()
    {
        var refs = CoreAssembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        Assert.DoesNotContain(refs, name => name.Contains("ModelContextProtocol", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Server_DoesNotReference_Cli()
    {
        var refs = ServerAssembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        Assert.DoesNotContain(refs, name => name == "DenMcp.Cli");
    }

    [Fact]
    public void Server_References_Core()
    {
        var refs = ServerAssembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        Assert.Contains("DenMcp.Core", refs);
    }

    [Fact]
    public void Cli_DoesNotReference_Server()
    {
        var refs = CliAssembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        Assert.DoesNotContain(refs, name => name == "DenMcp.Server");
    }

    [Fact]
    public void Cli_References_Core()
    {
        var refs = CliAssembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        Assert.Contains("DenMcp.Core", refs);
    }

    [Fact]
    public void Cli_DoesNotReference_AspNetCore()
    {
        var refs = CliAssembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        Assert.DoesNotContain(refs, name => name.Contains("AspNetCore", StringComparison.OrdinalIgnoreCase));
    }
}
