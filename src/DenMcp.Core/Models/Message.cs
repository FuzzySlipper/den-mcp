using System.Text.Json;

namespace DenMcp.Core.Models;

public sealed class Message
{
    public int Id { get; set; }
    public required string ProjectId { get; set; }
    public int? TaskId { get; set; }
    public int? ThreadId { get; set; }
    public required string Sender { get; set; }
    public required string Content { get; set; }
    public JsonElement? Metadata { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class Thread
{
    public required Message Root { get; set; }
    public required List<Message> Replies { get; set; }
}
