using System.Net;
using System.Text.Json;

namespace DenMcp.Server.CoreClient;

public sealed class DenCoreException : Exception
{
    public DenCoreException(
        string operation,
        string coreUrl,
        string message,
        bool retryable,
        HttpStatusCode? statusCode = null,
        string? responseBody = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Operation = operation;
        CoreUrl = coreUrl;
        Retryable = retryable;
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public string Operation { get; }
    public string CoreUrl { get; }
    public bool Retryable { get; }
    public HttpStatusCode? StatusCode { get; }
    public string? ResponseBody { get; }
}

public sealed record DenCoreToolError(
    string Error,
    bool Retryable,
    string CoreUrl,
    string Operation,
    string Message,
    int? StatusCode = null,
    string? CorrelationId = null)
{
    public static DenCoreToolError FromException(DenCoreException exception, string? correlationId = null) => new(
        Error: exception.Retryable ? "den_core_unavailable" : "den_core_error",
        Retryable: exception.Retryable,
        CoreUrl: exception.CoreUrl,
        Operation: exception.Operation,
        Message: exception.Message,
        StatusCode: exception.StatusCode is null ? null : (int)exception.StatusCode.Value,
        CorrelationId: correlationId);
}

public static class DenCoreToolErrorFormatter
{
    public static string Format(DenCoreException exception, string? correlationId = null) =>
        JsonSerializer.Serialize(DenCoreToolError.FromException(exception, correlationId), JsonOpts.Default);
}
