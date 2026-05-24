using Microsoft.AspNetCore.Http.Extensions;

namespace DenMcp.Server;

public static class DenCoreMcpProxyRoutes
{
    private const string ToolProfileQueryKey = "tool_profile";

    public static readonly IReadOnlySet<string> ProfileRouteNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "planner",
        "runner",
        "worker-coder",
        "worker-reviewer",
        "admin-current",
        "legacy-full"
    };

    public static void MapDenCoreMcpProxy(this WebApplication app)
    {
        var methods = new[] { "GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS" };

        // Stable, config-friendly profile URLs for Den-controlled Hermes profiles.
        // These routes rewrite to Core's canonical /mcp endpoint and add a
        // request-scoped tool_profile selector; Core remains authoritative for
        // tool metadata, tools/list filtering, and tools/call enforcement.
        app.MapMethods("/mcp/profiles/{profile}", methods, ProxyMcpToDenCoreAsync);
        app.MapMethods("/mcp/profiles/{profile}/{**path}", methods, ProxyMcpToDenCoreAsync);

        // Existing full-compatible endpoint and catch-all subpaths are preserved.
        app.MapMethods("/mcp", methods, ProxyMcpToDenCoreAsync);
        app.MapMethods("/mcp/{**path}", methods, ProxyMcpToDenCoreAsync);
    }

    public static async Task ProxyMcpToDenCoreAsync(HttpContext context, IHttpClientFactory httpClientFactory, DenCoreOptions coreOptions)
    {
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), BuildDenCoreMcpUri(coreOptions, context.Request));

        if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            request.Content = new StreamContent(context.Request.Body);
            foreach (var header in context.Request.Headers)
            {
                if (!header.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                    continue;
                request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }

        foreach (var header in context.Request.Headers)
        {
            if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                header.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                continue;
            request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }

        var client = httpClientFactory.CreateClient("DenCoreMcpProxy");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);

        context.Response.StatusCode = (int)response.StatusCode;
        foreach (var header in response.Headers)
            context.Response.Headers[header.Key] = header.Value.ToArray();
        foreach (var header in response.Content.Headers)
            context.Response.Headers[header.Key] = header.Value.ToArray();
        context.Response.Headers.Remove("transfer-encoding");

        await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    public static Uri BuildDenCoreMcpUri(DenCoreOptions coreOptions, HttpRequest request)
    {
        var baseUrl = string.IsNullOrWhiteSpace(coreOptions.BaseUrl)
            ? "http://localhost:5299"
            : coreOptions.BaseUrl.TrimEnd('/');

        var (targetPath, routeProfile) = RewriteFacadeProfilePath(request.Path);
        if (routeProfile is null)
            return new Uri(baseUrl + targetPath + request.QueryString);

        var query = new QueryBuilder();
        foreach (var parameter in request.Query)
        {
            if (parameter.Key.Equals(ToolProfileQueryKey, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var value in parameter.Value)
                query.Add(parameter.Key, value ?? string.Empty);
        }

        // A profile URL is itself the explicit selector. Remove any conflicting
        // query selector before forwarding so /mcp/profiles/runner cannot be
        // accidentally weakened by a copied query string.
        query.Add(ToolProfileQueryKey, routeProfile);
        return new Uri(baseUrl + targetPath + query.ToQueryString());
    }

    private static (string TargetPath, string? RouteProfile) RewriteFacadeProfilePath(PathString requestPath)
    {
        var path = requestPath.HasValue ? requestPath.Value! : "/mcp";
        const string prefix = "/mcp/profiles/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return (path, null);

        var remaining = path[prefix.Length..];
        var separator = remaining.IndexOf('/', StringComparison.Ordinal);
        var profile = separator < 0 ? remaining : remaining[..separator];
        var suffix = separator < 0 ? string.Empty : remaining[separator..];

        return ("/mcp" + suffix, profile);
    }
}
