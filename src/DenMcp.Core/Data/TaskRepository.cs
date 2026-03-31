using System.Text.Json;
using DenMcp.Core.Models;
using Microsoft.Data.Sqlite;
using TaskStatus = DenMcp.Core.Models.TaskStatus;

namespace DenMcp.Core.Data;

public interface ITaskRepository
{
    Task<ProjectTask> CreateAsync(ProjectTask task, int[]? dependsOn = null);
    Task<ProjectTask?> GetByIdAsync(int id);
    Task<TaskDetail> GetDetailAsync(int id);
    Task<List<TaskSummary>> ListAsync(string projectId, TaskStatus[]? statuses = null,
        string? assignedTo = null, string[]? tags = null, int? maxPriority = null, int? parentId = null);
    Task<ProjectTask> UpdateAsync(int id, Dictionary<string, object?> changes, string agent);
    Task AddDependencyAsync(int taskId, int dependsOn);
    Task RemoveDependencyAsync(int taskId, int dependsOn);
    Task<ProjectTask?> GetNextTaskAsync(string projectId, string? assignedTo = null);
}

public sealed class TaskRepository : ITaskRepository
{
    private readonly DbConnectionFactory _db;

    public TaskRepository(DbConnectionFactory db) => _db = db;

    public async Task<ProjectTask> CreateAsync(ProjectTask task, int[]? dependsOn = null)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tasks (project_id, parent_id, title, description, status, priority, assigned_to, tags)
            VALUES (@projectId, @parentId, @title, @description, @status, @priority, @assignedTo, @tags)
            RETURNING id, project_id, parent_id, title, description, status, priority, assigned_to, tags, created_at, updated_at
            """;
        cmd.Parameters.AddWithValue("@projectId", task.ProjectId);
        cmd.Parameters.AddWithValue("@parentId", (object?)task.ParentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@title", task.Title);
        cmd.Parameters.AddWithValue("@description", (object?)task.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@status", task.Status.ToDbValue());
        cmd.Parameters.AddWithValue("@priority", task.Priority);
        cmd.Parameters.AddWithValue("@assignedTo", (object?)task.AssignedTo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tags", task.Tags is { Count: > 0 } ? JsonSerializer.Serialize(task.Tags) : DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        var created = ReadTask(reader);
        await reader.CloseAsync();

        if (dependsOn is { Length: > 0 })
        {
            foreach (var depId in dependsOn)
            {
                await using var depCmd = conn.CreateCommand();
                depCmd.CommandText = "INSERT INTO task_dependencies (task_id, depends_on) VALUES (@taskId, @dependsOn)";
                depCmd.Parameters.AddWithValue("@taskId", created.Id);
                depCmd.Parameters.AddWithValue("@dependsOn", depId);
                await depCmd.ExecuteNonQueryAsync();
            }
        }

        await tx.CommitAsync();
        return created;
    }

    public async Task<ProjectTask?> GetByIdAsync(int id)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, project_id, parent_id, title, description, status, priority, assigned_to, tags, created_at, updated_at FROM tasks WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadTask(reader) : null;
    }

    public async Task<TaskDetail> GetDetailAsync(int id)
    {
        await using var conn = await _db.CreateConnectionAsync();

        // Main task
        await using var taskCmd = conn.CreateCommand();
        taskCmd.CommandText = "SELECT id, project_id, parent_id, title, description, status, priority, assigned_to, tags, created_at, updated_at FROM tasks WHERE id = @id";
        taskCmd.Parameters.AddWithValue("@id", id);
        await using var taskReader = await taskCmd.ExecuteReaderAsync();
        if (!await taskReader.ReadAsync())
            throw new KeyNotFoundException($"Task {id} not found");
        var task = ReadTask(taskReader);
        await taskReader.CloseAsync();

        // Dependencies
        await using var depCmd = conn.CreateCommand();
        depCmd.CommandText = """
            SELECT t.id, t.title, t.status
            FROM task_dependencies td
            JOIN tasks t ON t.id = td.depends_on
            WHERE td.task_id = @id
            """;
        depCmd.Parameters.AddWithValue("@id", id);
        var deps = new List<TaskDependencyInfo>();
        await using var depReader = await depCmd.ExecuteReaderAsync();
        while (await depReader.ReadAsync())
        {
            deps.Add(new TaskDependencyInfo
            {
                TaskId = depReader.GetInt32(0),
                Title = depReader.GetString(1),
                Status = EnumExtensions.ParseTaskStatus(depReader.GetString(2))
            });
        }
        await depReader.CloseAsync();

        // Subtasks
        await using var subCmd = conn.CreateCommand();
        subCmd.CommandText = """
            SELECT t.id, t.project_id, t.title, t.status, t.priority, t.assigned_to, t.parent_id, t.tags,
                   (SELECT COUNT(*) FROM task_dependencies WHERE task_id = t.id) as dep_count,
                   (SELECT COUNT(*) FROM tasks WHERE parent_id = t.id) as sub_count
            FROM tasks t WHERE t.parent_id = @id ORDER BY t.priority, t.id
            """;
        subCmd.Parameters.AddWithValue("@id", id);
        var subtasks = new List<TaskSummary>();
        await using var subReader = await subCmd.ExecuteReaderAsync();
        while (await subReader.ReadAsync())
            subtasks.Add(ReadTaskSummary(subReader));
        await subReader.CloseAsync();

        // Recent messages on this task
        await using var msgCmd = conn.CreateCommand();
        msgCmd.CommandText = """
            SELECT id, project_id, task_id, thread_id, sender, content, metadata, created_at
            FROM messages WHERE task_id = @id
            ORDER BY created_at DESC LIMIT 10
            """;
        msgCmd.Parameters.AddWithValue("@id", id);
        var messages = new List<Message>();
        await using var msgReader = await msgCmd.ExecuteReaderAsync();
        while (await msgReader.ReadAsync())
            messages.Add(ReadMessage(msgReader));

        return new TaskDetail
        {
            Task = task,
            Dependencies = deps,
            Subtasks = subtasks,
            RecentMessages = messages
        };
    }

    public async Task<List<TaskSummary>> ListAsync(string projectId, TaskStatus[]? statuses = null,
        string? assignedTo = null, string[]? tags = null, int? maxPriority = null, int? parentId = null)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();

        var where = new List<string> { "t.project_id = @projectId" };
        cmd.Parameters.AddWithValue("@projectId", projectId);

        if (statuses is { Length: > 0 })
        {
            var placeholders = new List<string>();
            for (var i = 0; i < statuses.Length; i++)
            {
                var p = $"@status{i}";
                placeholders.Add(p);
                cmd.Parameters.AddWithValue(p, statuses[i].ToDbValue());
            }
            where.Add($"t.status IN ({string.Join(", ", placeholders)})");
        }

        if (assignedTo is not null)
        {
            where.Add("t.assigned_to = @assignedTo");
            cmd.Parameters.AddWithValue("@assignedTo", assignedTo);
        }

        if (maxPriority is not null)
        {
            where.Add("t.priority <= @maxPriority");
            cmd.Parameters.AddWithValue("@maxPriority", maxPriority.Value);
        }

        if (parentId is not null)
        {
            where.Add("t.parent_id = @parentId");
            cmd.Parameters.AddWithValue("@parentId", parentId.Value);
        }
        else
        {
            where.Add("t.parent_id IS NULL");
        }

        // Tag filtering: task must have ALL specified tags
        if (tags is { Length: > 0 })
        {
            for (var i = 0; i < tags.Length; i++)
            {
                var p = $"@tag{i}";
                where.Add($"EXISTS (SELECT 1 FROM json_each(t.tags) WHERE json_each.value = {p})");
                cmd.Parameters.AddWithValue(p, tags[i]);
            }
        }

        cmd.CommandText = $"""
            SELECT t.id, t.project_id, t.title, t.status, t.priority, t.assigned_to, t.parent_id, t.tags,
                   (SELECT COUNT(*) FROM task_dependencies WHERE task_id = t.id) as dep_count,
                   (SELECT COUNT(*) FROM tasks WHERE parent_id = t.id) as sub_count
            FROM tasks t
            WHERE {string.Join(" AND ", where)}
            ORDER BY t.priority, t.id
            """;

        var results = new List<TaskSummary>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            results.Add(ReadTaskSummary(reader));
        return results;
    }

    public async Task<ProjectTask> UpdateAsync(int id, Dictionary<string, object?> changes, string agent)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();

        // Get current values for history
        var current = await GetByIdWithConnectionAsync(conn, id)
            ?? throw new KeyNotFoundException($"Task {id} not found");

        var sets = new List<string>();
        var paramIdx = 0;
        await using var cmd = conn.CreateCommand();

        foreach (var (field, newValue) in changes)
        {
            var (column, oldDbVal, newDbVal) = field switch
            {
                "title" => ("title", current.Title, (object?)(string?)newValue),
                "description" => ("description", (object?)current.Description, newValue),
                "status" => ("status", current.Status.ToDbValue(), ((TaskStatus)newValue!).ToDbValue()),
                "priority" => ("priority", (object)current.Priority, newValue),
                "assigned_to" => ("assigned_to", (object?)current.AssignedTo, newValue),
                "tags" => ("tags", current.Tags is { Count: > 0 } ? JsonSerializer.Serialize(current.Tags) : null,
                    newValue is List<string> t && t.Count > 0 ? JsonSerializer.Serialize(t) : null),
                "parent_id" => ("parent_id", (object?)current.ParentId, newValue),
                _ => throw new ArgumentException($"Unknown field: {field}")
            };

            var p = $"@p{paramIdx++}";
            sets.Add($"{column} = {p}");
            cmd.Parameters.AddWithValue(p, newDbVal ?? DBNull.Value);

            // Write history
            await using var histCmd = conn.CreateCommand();
            histCmd.CommandText = """
                INSERT INTO task_history (task_id, field, old_value, new_value, changed_by)
                VALUES (@taskId, @field, @oldValue, @newValue, @agent)
                """;
            histCmd.Parameters.AddWithValue("@taskId", id);
            histCmd.Parameters.AddWithValue("@field", field);
            histCmd.Parameters.AddWithValue("@oldValue", oldDbVal?.ToString() ?? (object)DBNull.Value);
            histCmd.Parameters.AddWithValue("@newValue", newDbVal?.ToString() ?? (object)DBNull.Value);
            histCmd.Parameters.AddWithValue("@agent", agent);
            await histCmd.ExecuteNonQueryAsync();
        }

        sets.Add("updated_at = datetime('now')");
        cmd.CommandText = $"""
            UPDATE tasks SET {string.Join(", ", sets)} WHERE id = @id
            RETURNING id, project_id, parent_id, title, description, status, priority, assigned_to, tags, created_at, updated_at
            """;
        cmd.Parameters.AddWithValue("@id", id);

        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        var updated = ReadTask(reader);
        await reader.CloseAsync();

        await tx.CommitAsync();
        return updated;
    }

    public async Task AddDependencyAsync(int taskId, int dependsOn)
    {
        await using var conn = await _db.CreateConnectionAsync();

        // Cycle detection: check if dependsOn can reach taskId
        if (await WouldCreateCycleAsync(conn, taskId, dependsOn))
            throw new InvalidOperationException($"Adding dependency {taskId} -> {dependsOn} would create a cycle");

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO task_dependencies (task_id, depends_on) VALUES (@taskId, @dependsOn)";
        cmd.Parameters.AddWithValue("@taskId", taskId);
        cmd.Parameters.AddWithValue("@dependsOn", dependsOn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task RemoveDependencyAsync(int taskId, int dependsOn)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM task_dependencies WHERE task_id = @taskId AND depends_on = @dependsOn";
        cmd.Parameters.AddWithValue("@taskId", taskId);
        cmd.Parameters.AddWithValue("@dependsOn", dependsOn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<ProjectTask?> GetNextTaskAsync(string projectId, string? assignedTo = null)
    {
        await using var conn = await _db.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();

        // Two-tier: first subtasks of in-progress parents, then top-level planned tasks.
        // Both filtered to tasks whose dependencies are all done.
        var assignedFilter = assignedTo is not null ? "AND t.assigned_to = @assignedTo" : "";

        cmd.CommandText = $"""
            WITH unblocked AS (
                SELECT t.id
                FROM tasks t
                WHERE t.project_id = @projectId
                  AND NOT EXISTS (
                    SELECT 1 FROM task_dependencies td
                    JOIN tasks dep ON dep.id = td.depends_on
                    WHERE td.task_id = t.id AND dep.status != 'done'
                  )
            ),
            candidates AS (
                -- Tier 1: subtasks of in-progress parents
                SELECT t.id, t.project_id, t.parent_id, t.title, t.description, t.status, t.priority, t.assigned_to, t.tags, t.created_at, t.updated_at,
                       0 as tier,
                       (SELECT COUNT(*) FROM task_dependencies WHERE task_id = t.id) as dep_count
                FROM tasks t
                JOIN tasks parent ON parent.id = t.parent_id AND parent.status = 'in_progress'
                WHERE t.project_id = @projectId
                  AND t.status IN ('planned', 'in_progress')
                  AND t.id IN (SELECT id FROM unblocked)
                  {assignedFilter}
                UNION ALL
                -- Tier 2: top-level planned tasks
                SELECT t.id, t.project_id, t.parent_id, t.title, t.description, t.status, t.priority, t.assigned_to, t.tags, t.created_at, t.updated_at,
                       1 as tier,
                       (SELECT COUNT(*) FROM task_dependencies WHERE task_id = t.id) as dep_count
                FROM tasks t
                WHERE t.project_id = @projectId
                  AND t.parent_id IS NULL
                  AND t.status = 'planned'
                  AND t.id IN (SELECT id FROM unblocked)
                  {assignedFilter}
            )
            SELECT id, project_id, parent_id, title, description, status, priority, assigned_to, tags, created_at, updated_at
            FROM candidates
            ORDER BY tier ASC, priority ASC, dep_count ASC, id ASC
            LIMIT 1
            """;

        cmd.Parameters.AddWithValue("@projectId", projectId);
        if (assignedTo is not null)
            cmd.Parameters.AddWithValue("@assignedTo", assignedTo);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadTask(reader) : null;
    }

    private static async Task<bool> WouldCreateCycleAsync(SqliteConnection conn, int taskId, int dependsOn)
    {
        // BFS from dependsOn through its dependencies. If we reach taskId, it's a cycle.
        var visited = new HashSet<int>();
        var queue = new Queue<int>();
        queue.Enqueue(dependsOn);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current == taskId)
                return true;
            if (!visited.Add(current))
                continue;

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT depends_on FROM task_dependencies WHERE task_id = @id";
            cmd.Parameters.AddWithValue("@id", current);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                queue.Enqueue(reader.GetInt32(0));
        }

        return false;
    }

    private static async Task<ProjectTask?> GetByIdWithConnectionAsync(SqliteConnection conn, int id)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, project_id, parent_id, title, description, status, priority, assigned_to, tags, created_at, updated_at FROM tasks WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadTask(reader) : null;
    }

    internal static ProjectTask ReadTask(SqliteDataReader reader)
    {
        var tagsJson = reader.IsDBNull(8) ? null : reader.GetString(8);
        return new ProjectTask
        {
            Id = reader.GetInt32(0),
            ProjectId = reader.GetString(1),
            ParentId = reader.IsDBNull(2) ? null : reader.GetInt32(2),
            Title = reader.GetString(3),
            Description = reader.IsDBNull(4) ? null : reader.GetString(4),
            Status = EnumExtensions.ParseTaskStatus(reader.GetString(5)),
            Priority = reader.GetInt32(6),
            AssignedTo = reader.IsDBNull(7) ? null : reader.GetString(7),
            Tags = tagsJson is not null ? JsonSerializer.Deserialize<List<string>>(tagsJson) : null,
            CreatedAt = DateTime.Parse(reader.GetString(9)),
            UpdatedAt = DateTime.Parse(reader.GetString(10))
        };
    }

    private static TaskSummary ReadTaskSummary(SqliteDataReader reader)
    {
        var tagsJson = reader.IsDBNull(7) ? null : reader.GetString(7);
        return new TaskSummary
        {
            Id = reader.GetInt32(0),
            ProjectId = reader.GetString(1),
            Title = reader.GetString(2),
            Status = EnumExtensions.ParseTaskStatus(reader.GetString(3)),
            Priority = reader.GetInt32(4),
            AssignedTo = reader.IsDBNull(5) ? null : reader.GetString(5),
            ParentId = reader.IsDBNull(6) ? null : reader.GetInt32(6),
            Tags = tagsJson is not null ? JsonSerializer.Deserialize<List<string>>(tagsJson) : null,
            DependencyCount = reader.GetInt32(8),
            SubtaskCount = reader.GetInt32(9)
        };
    }

    internal static Message ReadMessage(SqliteDataReader reader)
    {
        var metaJson = reader.IsDBNull(6) ? null : reader.GetString(6);
        return new Message
        {
            Id = reader.GetInt32(0),
            ProjectId = reader.GetString(1),
            TaskId = reader.IsDBNull(2) ? null : reader.GetInt32(2),
            ThreadId = reader.IsDBNull(3) ? null : reader.GetInt32(3),
            Sender = reader.GetString(4),
            Content = reader.GetString(5),
            Metadata = metaJson is not null ? JsonSerializer.Deserialize<JsonElement>(metaJson) : null,
            CreatedAt = DateTime.Parse(reader.GetString(7))
        };
    }
}
