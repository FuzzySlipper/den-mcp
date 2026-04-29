using Den.Bridge.Protocol;

namespace Den.Bridge.Tests;

public class BridgeBoundaryTests
{
    [Fact]
    public void BridgeProject_DoesNotReferenceDenMcpDomainOrUiAssemblies()
    {
        var refs = typeof(BridgeProtocol).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.DoesNotContain(refs, name => name.StartsWith("DenMcp.", StringComparison.Ordinal));
        Assert.DoesNotContain(refs, name => name.Contains("Electron", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(refs, name => name.Contains("WebView", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(refs, name => name.Contains("AspNetCore", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(refs, name => name.Contains("Terminal.Gui", StringComparison.OrdinalIgnoreCase));
    }
}
