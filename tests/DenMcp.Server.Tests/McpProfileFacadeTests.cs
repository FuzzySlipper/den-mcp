using DenMcp.Server;
using DenMcp.Server.CoreClient;
using Microsoft.AspNetCore.Http;

namespace DenMcp.Server.Tests;

public sealed class McpProfileFacadeTests
{
    [Theory]
    [InlineData("planner")]
    [InlineData("runner")]
    [InlineData("worker-coder")]
    [InlineData("worker-reviewer")]
    [InlineData("admin-current")]
    [InlineData("legacy-full")]
    public void ProfileRoute_RewritesToCoreMcpWithToolProfileSelector(string profile)
    {
        var request = CreateRequest($"/mcp/profiles/{profile}");

        var uri = DenCoreMcpProxyRoutes.BuildDenCoreMcpUri(CoreOptions(), request);

        Assert.Equal($"http://den-core.test/mcp?tool_profile={profile}", uri.ToString());
    }

    [Fact]
    public void ProfileRoute_PreservesMcpSubpathAndExistingQuery()
    {
        var request = CreateRequest("/mcp/profiles/runner/session/abc?cursor=next&verbose=true");

        var uri = DenCoreMcpProxyRoutes.BuildDenCoreMcpUri(CoreOptions(), request);

        Assert.Equal("/mcp/session/abc", uri.AbsolutePath);
        Assert.Contains("cursor=next", uri.Query);
        Assert.Contains("verbose=true", uri.Query);
        Assert.Contains("tool_profile=runner", uri.Query);
    }

    [Fact]
    public void ProfileRoute_ProfileSelectorOverridesConflictingQuerySelector()
    {
        var request = CreateRequest("/mcp/profiles/runner?tool_profile=legacy-full&tool_bundles=core-read");

        var uri = DenCoreMcpProxyRoutes.BuildDenCoreMcpUri(CoreOptions(), request);

        Assert.Equal("/mcp", uri.AbsolutePath);
        Assert.DoesNotContain("tool_profile=legacy-full", uri.Query);
        Assert.Contains("tool_profile=runner", uri.Query);
        Assert.Contains("tool_bundles=core-read", uri.Query);
    }

    [Theory]
    [InlineData("/mcp")]
    [InlineData("/mcp?tool_profile=planner")]
    [InlineData("/mcp?tool_bundles=core-read,review")]
    [InlineData("/mcp/session/abc?cursor=next")]
    public void CompatibleMcpRoute_ForwardsPathAndQueryUnchanged(string pathAndQuery)
    {
        var request = CreateRequest(pathAndQuery);

        var uri = DenCoreMcpProxyRoutes.BuildDenCoreMcpUri(CoreOptions(), request);

        Assert.Equal("http://den-core.test" + pathAndQuery, uri.ToString());
    }

    [Fact]
    public void ProfileRouteNames_DocumentsHermesConfigFriendlyFacadeEndpoints()
    {
        Assert.Contains("planner", DenCoreMcpProxyRoutes.ProfileRouteNames);
        Assert.Contains("runner", DenCoreMcpProxyRoutes.ProfileRouteNames);
        Assert.Contains("worker-coder", DenCoreMcpProxyRoutes.ProfileRouteNames);
        Assert.Contains("worker-reviewer", DenCoreMcpProxyRoutes.ProfileRouteNames);
        Assert.Contains("admin-current", DenCoreMcpProxyRoutes.ProfileRouteNames);
        Assert.Contains("legacy-full", DenCoreMcpProxyRoutes.ProfileRouteNames);
    }

    private static DenCoreOptions CoreOptions() => new() { BaseUrl = "http://den-core.test/" };

    private static HttpRequest CreateRequest(string pathAndQuery)
    {
        var uri = new Uri("http://adapter.test" + pathAndQuery);
        var context = new DefaultHttpContext();
        context.Request.Scheme = uri.Scheme;
        context.Request.Host = new HostString(uri.Host);
        context.Request.Path = uri.AbsolutePath;
        context.Request.QueryString = new QueryString(uri.Query);
        return context.Request;
    }
}
