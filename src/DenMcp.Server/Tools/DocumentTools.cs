using System.ComponentModel;
using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using DenMcp.Server.CoreClient;
using ModelContextProtocol.Server;

namespace DenMcp.Server.Tools;

[McpServerToolType]
public sealed class DocumentTools
{
    [McpServerTool(Name = "store_document"), Description("Create or update a document. If a document with the same project_id + slug exists, it is overwritten.")]
    public static async Task<string> StoreDocument(
        DenCoreClient coreClient,
        [Description("Project or space ID. Use '_global' for cross-project docs.")] string project_id,
        [Description("Unique slug within the project, e.g. 'damage-system-spec'.")] string slug,
        [Description("Document title.")] string title,
        [Description("Document content (markdown).")] string content,
        [Description("Document type: prd, spec, adr, convention, reference, note, memory. Default: spec.")] string doc_type = "spec",
        [Description("JSON array of string tags.")] string? tags = null,
        [Description("Optional short summary for indexing and listing.")] string? summary = null,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        try
        {
            var parsedTags = tags is not null ? JsonSerializer.Deserialize<List<string>>(tags) : null;
            var doc = await coreClient.StoreDocumentAsync(project_id, new
            {
                slug,
                title,
                content,
                doc_type,
                tags = parsedTags,
                summary
            });
            return verbose
                ? JsonSerializer.Serialize(doc, JsonOpts.Default)
                : ConciseResponse.StoredDocument(doc);
        }
        catch (DenCoreException ex)
        {
            return DenCoreToolErrorFormatter.Format(ex);
        }
    }

    [McpServerTool(Name = "get_document"), Description("Get a document's full content by project or space ID and slug.")]
    public static async Task<string> GetDocument(
        DenCoreClient coreClient,
        [Description("Project or space ID.")] string project_id,
        [Description("Document slug.")] string slug)
    {
        try
        {
            var doc = await coreClient.GetDocumentAsync(project_id, slug);
            return JsonSerializer.Serialize(doc, JsonOpts.Default);
        }
        catch (DenCoreException ex)
        {
            return DenCoreToolErrorFormatter.Format(ex);
        }
    }

    [McpServerTool(Name = "list_documents"), Description("List document summaries (without content). Excludes archived documents by default. Omit project_id to list across all projects and spaces. Concise by default with slug/title/doc_type/tags/summary; use verbose=true for full document records.")]
    public static async Task<string> ListDocuments(
        DenCoreClient coreClient,
        [Description("Project or space ID. Omit to list across all projects and spaces.")] string? project_id = null,
        [Description("Filter by type: prd, spec, adr, convention, reference, note.")] string? doc_type = null,
        [Description("Filter by tags (comma-separated). Document must have ALL specified tags.")] string? tags = null,
        [Description("If true, return full JSON records. Default is concise with slug/title/doc_type/tags/summary.")] bool verbose = false)
    {
        try
        {
            var docs = await coreClient.ListDocumentsAsync(project_id, doc_type, tags);
            return verbose
                ? JsonSerializer.Serialize(docs, JsonOpts.Default)
                : ProjectDocumentList(docs);
        }
        catch (DenCoreException ex)
        {
            return DenCoreToolErrorFormatter.Format(ex);
        }
    }

    [McpServerTool(Name = "search_documents"), Description("Full-text search across documents. Excludes archived documents. Supports AND, OR, NOT, and \"phrase\" queries. Concise by default with slug/title/doc_type/snippet; use verbose=true for full results.")]
    public static async Task<string> SearchDocuments(
        DenCoreClient coreClient,
        [Description("FTS5 search query.")] string query,
        [Description("Scope search to one project or space.")] string? project_id = null,
        [Description("If true, return full JSON records. Default is concise with slug/title/doc_type/snippet.")] bool verbose = false)
    {
        try
        {
            var results = await coreClient.SearchDocumentsAsync(query, project_id);
            return verbose
                ? JsonSerializer.Serialize(results, JsonOpts.Default)
                : ProjectSearchResults(results);
        }
        catch (DenCoreException ex)
        {
            return DenCoreToolErrorFormatter.Format(ex);
        }
    }

    private static string ProjectDocumentList(JsonElement docs)
    {
        var items = new List<object>();
        if (docs.TryGetProperty("items", out var itemsArray))
        {
            foreach (var doc in itemsArray.EnumerateArray())
            {
                items.Add(new
                {
                    project_id = GetString(doc, "project_id"),
                    slug = GetString(doc, "slug"),
                    title = GetString(doc, "title"),
                    doc_type = GetString(doc, "doc_type"),
                    visibility = GetString(doc, "visibility"),
                    tags = GetJsonArray(doc, "tags"),
                    summary = GetString(doc, "summary"),
                    updated_at = GetString(doc, "updated_at"),
                });
            }
        }
        return JsonSerializer.Serialize(new { items, count = items.Count }, JsonOpts.Default);
    }

    private static string ProjectSearchResults(JsonElement results)
    {
        var items = new List<object>();
        if (results.TryGetProperty("results", out var resultsArray))
        {
            foreach (var r in resultsArray.EnumerateArray())
            {
                items.Add(new
                {
                    project_id = GetString(r, "project_id"),
                    slug = GetString(r, "slug"),
                    title = GetString(r, "title"),
                    doc_type = GetString(r, "doc_type"),
                    snippet = GetString(r, "snippet") ?? Truncate(GetString(r, "content"), 300),
                });
            }
        }
        return JsonSerializer.Serialize(new { count = items.Count, results = items }, JsonOpts.Default);
    }

    private static string? GetString(JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString();
        return null;
    }

    private static object? GetJsonArray(JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Array)
            return prop;
        return null;
    }

    private static string? Truncate(string? value, int maxLen)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        return value.Length <= maxLen ? value : value[..maxLen] + "…";
    }

    [McpServerTool(Name = "delete_document"), Description("Delete a document by project or space ID and slug.")]
    public static async Task<string> DeleteDocument(
        DenCoreClient coreClient,
        [Description("Project or space ID.")] string project_id,
        [Description("Document slug.")] string slug)
    {
        try
        {
            var deleted = await coreClient.DeleteDocumentAsync(project_id, slug);
            return JsonSerializer.Serialize(deleted, JsonOpts.Default);
        }
        catch (DenCoreException ex)
        {
            return DenCoreToolErrorFormatter.Format(ex);
        }
    }

    public static async Task<string> StoreDocument(
        IDocumentRepository repo,
        string project_id,
        string slug,
        string title,
        string content,
        string doc_type = "spec",
        string? tags = null,
        string? summary = null,
        bool verbose = false)
    {
        var parsedTags = tags is not null ? JsonSerializer.Deserialize<List<string>>(tags) : null;
        var doc = await repo.UpsertAsync(new Document
        {
            ProjectId = project_id,
            Slug = slug,
            Title = title,
            Content = content,
            DocType = EnumExtensions.ParseDocType(doc_type),
            Tags = parsedTags,
            Summary = summary
        });
        return verbose ? JsonSerializer.Serialize(doc, JsonOpts.Default) : ConciseResponse.StoredDocument(doc);
    }

    public static async Task<string> GetDocument(IDocumentRepository repo, string project_id, string slug)
    {
        var doc = await repo.GetAsync(project_id, slug);
        if (doc is null)
            return JsonSerializer.Serialize(new { error = $"Document '{slug}' not found in project '{project_id}'." }, JsonOpts.Default);
        return JsonSerializer.Serialize(doc, JsonOpts.Default);
    }

    public static async Task<string> ListDocuments(IDocumentRepository repo, string? project_id = null, string? doc_type = null, string? tags = null)
    {
        var parsedType = doc_type is not null ? EnumExtensions.ParseDocType(doc_type) : (DocType?)null;
        var tagList = tags?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var docs = await repo.ListAsync(project_id, parsedType, tagList);
        return JsonSerializer.Serialize(docs, JsonOpts.Default);
    }

    public static async Task<string> SearchDocuments(IDocumentRepository repo, string query, string? project_id = null)
    {
        var results = await repo.SearchAsync(query, project_id);
        return JsonSerializer.Serialize(results, JsonOpts.Default);
    }

    public static async Task<string> DeleteDocument(IDocumentRepository repo, string project_id, string slug)
    {
        var deleted = await repo.DeleteAsync(project_id, slug);
        return deleted
            ? JsonSerializer.Serialize(new { message = $"Document '{slug}' deleted from project '{project_id}'." }, JsonOpts.Default)
            : JsonSerializer.Serialize(new { error = $"Document '{slug}' not found in project '{project_id}'." }, JsonOpts.Default);
    }

}
