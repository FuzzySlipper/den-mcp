using System.Text.Json;
using DenMcp.Core.Models;
using Microsoft.Data.Sqlite;

namespace DenMcp.Core.Data;

public interface IReviewRoundRepository
{
    Task<ReviewRound> CreateAsync(CreateReviewRoundInput input);
    Task<ReviewRound?> GetByIdAsync(int id);
    Task<List<ReviewRound>> ListByTaskAsync(int taskId);
    Task<ReviewRound?> GetLatestByTaskAsync(int taskId);
    Task<ReviewRound> SetVerdictAsync(int id, ReviewVerdict verdict, string decidedBy, string? notes = null);
}

public sealed class ReviewRoundRepository : IReviewRoundRepository
{
    private readonly DbConnectionFactory _db;

    public ReviewRoundRepository(DbConnectionFactory db) => _db = db;

    public async Task<ReviewRound> CreateAsync(CreateReviewRoundInput input)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        var latest = await GetLatestByTaskWithConnectionAsync(conn, input.TaskId);
        var roundNumber = (latest?.RoundNumber ?? 0) + 1;
        var lastReviewedHead = input.LastReviewedHeadCommit ?? latest?.HeadCommit;

        if (lastReviewedHead is not null &&
            string.Equals(lastReviewedHead, input.HeadCommit, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Task {input.TaskId} review head {input.HeadCommit} matches the last reviewed head commit.");
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO review_rounds (
                task_id, round_number, requested_by, branch, base_branch, base_commit, head_commit,
                last_reviewed_head_commit, commits_since_last_review, tests_run, notes
            )
            VALUES (
                @taskId, @roundNumber, @requestedBy, @branch, @baseBranch, @baseCommit, @headCommit,
                @lastReviewedHeadCommit, @commitsSinceLastReview, @testsRun, @notes
            )
            RETURNING id, task_id, round_number, requested_by, branch, base_branch, base_commit,
                      head_commit, last_reviewed_head_commit, commits_since_last_review, tests_run,
                      notes, verdict, verdict_by, verdict_notes, requested_at, verdict_at
            """;
        cmd.Parameters.AddWithValue("@taskId", input.TaskId);
        cmd.Parameters.AddWithValue("@roundNumber", roundNumber);
        cmd.Parameters.AddWithValue("@requestedBy", input.RequestedBy);
        cmd.Parameters.AddWithValue("@branch", input.Branch);
        cmd.Parameters.AddWithValue("@baseBranch", input.BaseBranch);
        cmd.Parameters.AddWithValue("@baseCommit", input.BaseCommit);
        cmd.Parameters.AddWithValue("@headCommit", input.HeadCommit);
        cmd.Parameters.AddWithValue("@lastReviewedHeadCommit", (object?)lastReviewedHead ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@commitsSinceLastReview", (object?)input.CommitsSinceLastReview ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@testsRun", input.TestsRun is { Count: > 0 } ? JsonSerializer.Serialize(input.TestsRun) : DBNull.Value);
        cmd.Parameters.AddWithValue("@notes", (object?)input.Notes ?? DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        var created = ReadReviewRound(reader);
        await reader.CloseAsync();

        await tx.CommitAsync();
        return created;
    }

    public async Task<ReviewRound?> GetByIdAsync(int id)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, task_id, round_number, requested_by, branch, base_branch, base_commit,
                   head_commit, last_reviewed_head_commit, commits_since_last_review, tests_run,
                   notes, verdict, verdict_by, verdict_notes, requested_at, verdict_at
            FROM review_rounds WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", id);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadReviewRound(reader) : null;
    }

    public async Task<List<ReviewRound>> ListByTaskAsync(int taskId)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, task_id, round_number, requested_by, branch, base_branch, base_commit,
                   head_commit, last_reviewed_head_commit, commits_since_last_review, tests_run,
                   notes, verdict, verdict_by, verdict_notes, requested_at, verdict_at
            FROM review_rounds
            WHERE task_id = @taskId
            ORDER BY round_number ASC
            """;
        cmd.Parameters.AddWithValue("@taskId", taskId);

        var rounds = new List<ReviewRound>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rounds.Add(ReadReviewRound(reader));
        return rounds;
    }

    public async Task<ReviewRound?> GetLatestByTaskAsync(int taskId)
    {
        await using var conn = await _db.CreateConnectionAsync();
        return await GetLatestByTaskWithConnectionAsync(conn, taskId);
    }

    public async Task<ReviewRound> SetVerdictAsync(int id, ReviewVerdict verdict, string decidedBy, string? notes = null)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE review_rounds
            SET verdict = @verdict,
                verdict_by = @verdictBy,
                verdict_notes = @verdictNotes,
                verdict_at = datetime('now')
            WHERE id = @id
            RETURNING id, task_id, round_number, requested_by, branch, base_branch, base_commit,
                      head_commit, last_reviewed_head_commit, commits_since_last_review, tests_run,
                      notes, verdict, verdict_by, verdict_notes, requested_at, verdict_at
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@verdict", verdict.ToDbValue());
        cmd.Parameters.AddWithValue("@verdictBy", decidedBy);
        cmd.Parameters.AddWithValue("@verdictNotes", (object?)notes ?? DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new KeyNotFoundException($"Review round {id} not found");
        return ReadReviewRound(reader);
    }

    private static async Task<ReviewRound?> GetLatestByTaskWithConnectionAsync(SqliteConnection conn, int taskId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, task_id, round_number, requested_by, branch, base_branch, base_commit,
                   head_commit, last_reviewed_head_commit, commits_since_last_review, tests_run,
                   notes, verdict, verdict_by, verdict_notes, requested_at, verdict_at
            FROM review_rounds
            WHERE task_id = @taskId
            ORDER BY round_number DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@taskId", taskId);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadReviewRound(reader) : null;
    }

    internal static ReviewRound ReadReviewRound(SqliteDataReader reader)
    {
        var testsRunJson = reader.IsDBNull(10) ? null : reader.GetString(10);
        var verdictValue = reader.IsDBNull(12) ? null : reader.GetString(12);

        return new ReviewRound
        {
            Id = reader.GetInt32(0),
            TaskId = reader.GetInt32(1),
            RoundNumber = reader.GetInt32(2),
            RequestedBy = reader.GetString(3),
            Branch = reader.GetString(4),
            BaseBranch = reader.GetString(5),
            BaseCommit = reader.GetString(6),
            HeadCommit = reader.GetString(7),
            LastReviewedHeadCommit = reader.IsDBNull(8) ? null : reader.GetString(8),
            CommitsSinceLastReview = reader.IsDBNull(9) ? null : reader.GetInt32(9),
            TestsRun = testsRunJson is not null ? JsonSerializer.Deserialize<List<string>>(testsRunJson) : null,
            Notes = reader.IsDBNull(11) ? null : reader.GetString(11),
            Verdict = verdictValue is not null ? EnumExtensions.ParseReviewVerdict(verdictValue) : null,
            VerdictBy = reader.IsDBNull(13) ? null : reader.GetString(13),
            VerdictNotes = reader.IsDBNull(14) ? null : reader.GetString(14),
            RequestedAt = DateTime.Parse(reader.GetString(15)),
            VerdictAt = reader.IsDBNull(16) ? null : DateTime.Parse(reader.GetString(16))
        };
    }
}
