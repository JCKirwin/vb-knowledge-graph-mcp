using Microsoft.Data.Sqlite;

namespace VbKnowledgeGraph;

/// <summary>
/// Bridges VB.NET <c>Imports</c> declarations to C# nodes in a sibling knowledge-graph
/// SQLite database (e.g. codebase-memory-mcp). Opt-in via <see cref="BridgeConfig"/>.
///
/// The sibling DB is opened READ-ONLY; this class only writes into the local
/// <c>bridge_mappings</c> table on the VB index DB.
/// </summary>
public sealed class CSharpBridge
{
    private readonly SqliteStore _store;
    private readonly BridgeConfig _config;

    public CSharpBridge(SqliteStore store, BridgeConfig config)
    {
        _store = store;
        _config = config;
    }

    public BridgeResult BuildBridge()
    {
        var result = new BridgeResult();

        if (!_config.IsUsable())
        {
            result.Error = "Bridge config missing required fields (cmmDbPath, cmmProjectKey).";
            return result;
        }

        if (!File.Exists(_config.CmmDbPath))
        {
            result.Error = $"Sibling C# graph DB not found at: {_config.CmmDbPath}";
            return result;
        }

        // Pull VB import namespaces matching any of the configured filter patterns.
        var filterClauses = _config.NamespaceFilters
            .Select((_, i) => $"namespace LIKE @p{i}")
            .ToList();
        var whereClause = filterClauses.Count > 0
            ? "WHERE " + string.Join(" OR ", filterClauses)
            : "";

        var importSql = $"""
            SELECT DISTINCT namespace FROM imports
            {whereClause}
            ORDER BY namespace
            """;
        var importParams = _config.NamespaceFilters
            .Select((p, i) => ($"@p{i}", (object?)p))
            .ToArray();

        var imports = _store.Query(importSql, importParams);
        result.DistinctImports = imports.Count;

        // Build label IN-clause defensively (sanitize: alpha only).
        var labels = _config.CsharpLabels
            .Where(l => !string.IsNullOrWhiteSpace(l) && l.All(char.IsLetter))
            .ToList();
        if (labels.Count == 0) labels.Add("Class");
        var labelInClause = string.Join(",", labels.Select(l => $"'{l}'"));

        using var cmmConn = new SqliteConnection($"Data Source={_config.CmmDbPath};Mode=ReadOnly");
        cmmConn.Open();

        _store.BeginTransaction();
        try
        {
            using (var clearCmd = _store.Connection.CreateCommand())
            {
                clearCmd.CommandText = "DELETE FROM bridge_mappings";
                clearCmd.ExecuteNonQuery();
            }

            foreach (var import in imports)
            {
                var ns = (string)import["namespace"]!;
                var pattern = $"%{ns}.%";

                using var cmd = cmmConn.CreateCommand();
                cmd.CommandText = $"""
                    SELECT id, qualified_name, name, label, file_path
                    FROM nodes
                    WHERE project = @project
                      AND label IN ({labelInClause})
                      AND qualified_name LIKE @pattern
                    """;
                cmd.Parameters.AddWithValue("@project", _config.CmmProjectKey!);
                cmd.Parameters.AddWithValue("@pattern", pattern);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var nodeId = reader.GetInt64(0);
                    var qn = reader.GetString(1);
                    var name = reader.GetString(2);
                    var label = reader.GetString(3);
                    var filePath = reader.IsDBNull(4) ? null : reader.GetString(4);

                    _store.InsertBridgeMapping(ns, nodeId, qn, name, label, filePath);
                    result.MappingsCreated++;
                }
            }

            _store.CommitTransaction();
        }
        catch
        {
            try
            {
                using var rollback = _store.Connection.CreateCommand();
                rollback.CommandText = "ROLLBACK";
                rollback.ExecuteNonQuery();
            }
            catch { /* swallow */ }
            throw;
        }

        var coverage = _store.Query("""
            SELECT COUNT(DISTINCT i.file_id) FROM imports i
            INNER JOIN bridge_mappings b ON b.vb_import_namespace = i.namespace
            """);
        result.FilesWithBridge = Convert.ToInt32(coverage[0].Values.First()!);

        return result;
    }
}

public sealed class BridgeResult
{
    public int DistinctImports { get; set; }
    public int MappingsCreated { get; set; }
    public int FilesWithBridge { get; set; }
    public string? Error { get; set; }
}
