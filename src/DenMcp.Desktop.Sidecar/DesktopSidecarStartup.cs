using Den.Bridge.Protocol;

namespace DenMcp.Desktop.Sidecar;

public static class DesktopSidecarStartup
{
    public static string FormatReadySentinel(DesktopSidecarReadySentinel sentinel)
    {
        ArgumentNullException.ThrowIfNull(sentinel);
        return DesktopSidecarProtocol.ReadySentinelPrefix + BridgeJson.Serialize(sentinel);
    }

    public static DesktopSidecarReadySentinel CreateReadySentinel(DesktopSidecarOptions options, int port)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new DesktopSidecarReadySentinel
        {
            Port = port,
            EndpointPath = options.EndpointPath,
            ProtocolVersion = BridgeProtocol.ProtocolVersion,
            SchemaVersion = DesktopSidecarProtocol.SchemaVersion,
            SchemaBundleId = DesktopSidecarProtocol.SchemaBundleId,
            AppId = options.AppId,
            AppVersion = options.AppVersion,
        };
    }
}
