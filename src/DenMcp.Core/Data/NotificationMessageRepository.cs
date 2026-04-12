using Microsoft.Data.Sqlite;

namespace DenMcp.Core.Data;

public interface INotificationMessageRepository
{
    Task LinkDispatchMessageAsync(string channel, string externalMessageId, int dispatchId, string? recipient = null);
    Task<int?> FindDispatchIdAsync(string channel, string externalMessageId);
}

public sealed class NotificationMessageRepository : INotificationMessageRepository
{
    private readonly DbConnectionFactory _db;

    public NotificationMessageRepository(DbConnectionFactory db) => _db = db;

    public async Task LinkDispatchMessageAsync(string channel, string externalMessageId, int dispatchId, string? recipient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalMessageId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dispatchId);

        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO notification_message_links
                (channel, external_message_id, dispatch_id, recipient)
            VALUES
                (@channel, @externalMessageId, @dispatchId, @recipient)
            ON CONFLICT(channel, external_message_id) DO UPDATE SET
                dispatch_id = excluded.dispatch_id,
                recipient = excluded.recipient
            """;
        cmd.Parameters.AddWithValue("@channel", channel);
        cmd.Parameters.AddWithValue("@externalMessageId", externalMessageId);
        cmd.Parameters.AddWithValue("@dispatchId", dispatchId);
        cmd.Parameters.AddWithValue("@recipient", (object?)recipient ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<int?> FindDispatchIdAsync(string channel, string externalMessageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalMessageId);

        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT dispatch_id
            FROM notification_message_links
            WHERE channel = @channel AND external_message_id = @externalMessageId
            """;
        cmd.Parameters.AddWithValue("@channel", channel);
        cmd.Parameters.AddWithValue("@externalMessageId", externalMessageId);

        var result = await cmd.ExecuteScalarAsync();
        return result is null || result is DBNull ? null : Convert.ToInt32(result);
    }
}
