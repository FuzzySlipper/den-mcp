using System.Reflection;
using DenMcp.Desktop.Sidecar;

namespace DenMcp.Desktop.Sidecar.Tests;

public class DesktopSidecarBoundaryTests
{
    [Fact]
    public void Sidecar_DoesNotReferenceElectronOrDesktopRendererPackages()
    {
        var refs = typeof(DesktopSidecarOptions).Assembly.GetReferencedAssemblies().Select(Name).ToArray();

        Assert.DoesNotContain(refs, name => name.Contains("Electron", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(refs, name => name.Contains("Tauri", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(refs, name => name.Contains("WebView", StringComparison.OrdinalIgnoreCase));
    }

    private static string Name(AssemblyName assemblyName)
    {
        return assemblyName.Name ?? string.Empty;
    }
}
