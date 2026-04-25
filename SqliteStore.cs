using Microsoft.Data.Sqlite;

namespace VbKnowledgeGraph;

/// <summary>
/// SQLite persistence layer for the VB.NET knowledge graph.
/// Stores symbols (classes, methods, properties, etc.) and edges (inherits, implements, handles, calls).
/// Uses FTS5 for full-text search on symbol names and content.
/// </summary>
public sealed class SqliteStore : IDisposable
{
    private readonly SqliteConnection _conn;

    public SqliteStore(string dbPath)
    {
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        InitializeSchema();
    }

    private void InitializeSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;

            CREATE TABLE IF NOT EXISTS files (
                id INTEGER PRIMARY KEY,
                path TEXT NOT NULL UNIQUE,
                last_indexed TEXT NOT NULL,
                file_hash TEXT,
                file_size INTEGER
            );

            CREATE TABLE IF NOT EXISTS symbols (
                id INTEGER PRIMARY KEY,
                file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                kind TEXT NOT NULL,
                name TEXT NOT NULL,
                qualified_name TEXT NOT NULL,
                namespace TEXT,
                signature TEXT,
                return_type TEXT,
                access TEXT,
                modifiers TEXT,
                line_start INTEGER NOT NULL,
                line_end INTEGER NOT NULL,
                parent_symbol_id INTEGER REFERENCES symbols(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_symbols_kind ON symbols(kind);
            CREATE INDEX IF NOT EXISTS idx_symbols_name ON symbols(name);
            CREATE INDEX IF NOT EXISTS idx_symbols_qn ON symbols(qualified_name);
            CREATE INDEX IF NOT EXISTS idx_symbols_file ON symbols(file_id);
            CREATE INDEX IF NOT EXISTS idx_symbols_parent ON symbols(parent_symbol_id);

            CREATE TABLE IF NOT EXISTS edges (
                id INTEGER PRIMARY KEY,
                source_id INTEGER NOT NULL REFERENCES symbols(id) ON DELETE CASCADE,
                target_name TEXT NOT NULL,
                kind TEXT NOT NULL,
                target_id INTEGER REFERENCES symbols(id) ON DELETE SET NULL
            );

            CREATE INDEX IF NOT EXISTS idx_edges_source ON edges(source_id);
            CREATE INDEX IF NOT EXISTS idx_edges_target_name ON edges(target_name);
            CREATE INDEX IF NOT EXISTS idx_edges_kind ON edges(kind);
            CREATE INDEX IF NOT EXISTS idx_edges_target_id ON edges(target_id);

            CREATE TABLE IF NOT EXISTS imports (
                id INTEGER PRIMARY KEY,
                file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                namespace TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_imports_ns ON imports(namespace);
            """;
        cmd.ExecuteNonQuery();

        using var ftsCmd = _conn.CreateCommand();
        ftsCmd.CommandText = """
            CREATE VIRTUAL TABLE IF NOT EXISTS symbols_fts USING fts5(
                name,
                qualified_name,
                kind,
                signature,
                content=symbols,
                content_rowid=id
            );
            """;
        ftsCmd.ExecuteNonQuery();
    }

    public void ClearAll()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM edges; DELETE FROM imports; DELETE FROM symbols; DELETE FROM files; DELETE FROM symbols_fts;";
        cmd.ExecuteNonQuery();
    }

    public long InsertFile(string path, string lastIndexed, string? fileHash = null, long? fileSize = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO files (path, last_indexed, file_hash, file_size)
            VALUES (@path, @ts, @hash, @size)
            ON CONFLICT(path) DO UPDATE SET last_indexed=@ts, file_hash=@hash, file_size=@size
            RETURNING id
            """;
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@ts", lastIndexed);
        cmd.Parameters.AddWithValue("@hash", (object?)fileHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@size", (object?)fileSize ?? DBNull.Value);
        return (long)cmd.ExecuteScalar()!;
    }

    public string? GetFileHash(string path)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT file_hash FROM files WHERE path = @path";
        cmd.Parameters.AddWithValue("@path", path);
        var result = cmd.ExecuteScalar();
        return result is DBNull or null ? null : (string)result;
    }

    public void DeleteFileData(long fileId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM edges WHERE source_id IN (SELECT id FROM symbols WHERE file_id = @fid);
            DELETE FROM imports WHERE file_id = @fid;
            DELETE FROM symbols WHERE file_id = @fid;
            """;
        cmd.Parameters.AddWithValue("@fid", fileId);
        cmd.ExecuteNonQuery();
    }

    public int PurgeDeletedFiles(ISet<string> existingPaths)
    {
        var allFiles = Query("SELECT id, path FROM files");
        int purged = 0;
        foreach (var file in allFiles)
        {
            var path = (string)file["path"]!;
            if (!existingPaths.Contains(path))
            {
                var fileId = (long)file["id"]!;
                DeleteFileData(fileId);
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "DELETE FROM files WHERE id = @id";
                cmd.Parameters.AddWithValue("@id", fileId);
                cmd.ExecuteNonQuery();
                purged++;
            }
        }
        return purged;
    }

    public long InsertSymbol(long fileId, string kind, string name, string qualifiedName,
        string? ns, string? signature, string? returnType, string? access, string? modifiers,
        int lineStart, int lineEnd, long? parentSymbolId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO symbols (file_id, kind, name, qualified_name, namespace, signature,
                return_type, access, modifiers, line_start, line_end, parent_symbol_id)
            VALUES (@fid, @kind, @name, @qn, @ns, @sig, @rt, @acc, @mod, @ls, @le, @pid)
            RETURNING id
            """;
        cmd.Parameters.AddWithValue("@fid", fileId);
        cmd.Parameters.AddWithValue("@kind", kind);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@qn", qualifiedName);
        cmd.Parameters.AddWithValue("@ns", (object?)ns ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sig", (object?)signature ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@rt", (object?)returnType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@acc", (object?)access ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@mod", (object?)modifiers ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ls", lineStart);
        cmd.Parameters.AddWithValue("@le", lineEnd);
        cmd.Parameters.AddWithValue("@pid", (object?)parentSymbolId ?? DBNull.Value);
        return (long)cmd.ExecuteScalar()!;
    }

    public void InsertEdge(long sourceId, string targetName, string kind, long? targetId = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO edges (source_id, target_name, kind, target_id) VALUES (@sid, @tn, @kind, @tid)";
        cmd.Parameters.AddWithValue("@sid", sourceId);
        cmd.Parameters.AddWithValue("@tn", targetName);
        cmd.Parameters.AddWithValue("@kind", kind);
        cmd.Parameters.AddWithValue("@tid", (object?)targetId ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void InsertImport(long fileId, string ns)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO imports (file_id, namespace) VALUES (@fid, @ns)";
        cmd.Parameters.AddWithValue("@fid", fileId);
        cmd.Parameters.AddWithValue("@ns", ns);
        cmd.ExecuteNonQuery();
    }

    public void RebuildFts()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO symbols_fts(symbols_fts) VALUES('rebuild')";
        cmd.ExecuteNonQuery();
    }

    public int ResolveEdges()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE edges SET target_id = (
                SELECT s.id FROM symbols s WHERE s.qualified_name = edges.target_name LIMIT 1
            ) WHERE target_id IS NULL
            """;
        return cmd.ExecuteNonQuery();
    }

    public SqliteConnection Connection => _conn;

    public void BeginTransaction() => _conn.CreateCommand().Let(c => { c.CommandText = "BEGIN"; c.ExecuteNonQuery(); });
    public void CommitTransaction() => _conn.CreateCommand().Let(c => { c.CommandText = "COMMIT"; c.ExecuteNonQuery(); });

    public List<Dictionary<string, object?>> Query(string sql, params (string name, object? value)[] parameters)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);

        var results = new List<Dictionary<string, object?>>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            results.Add(row);
        }
        return results;
    }

    public void Dispose() => _conn.Dispose();
}

internal static class SqliteCommandExtensions
{
    public static void Let(this SqliteCommand cmd, Action<SqliteCommand> action)
    {
        action(cmd);
        cmd.Dispose();
    }
}
