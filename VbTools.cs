using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

namespace VbKnowledgeGraph;

[McpServerToolType]
public sealed class VbTools
{
    private static SqliteStore Store => ServerState.Store ?? throw new InvalidOperationException("Index not loaded. Run reindex first.");
    private static string RootPath => ServerState.RootPath;

    [McpServerTool(Name = "search_vb_symbols"), Description(
        "Search VB.NET symbols (classes, modules, methods, properties, etc.) by name pattern. " +
        "Uses full-text search. Returns up to 50 matches with file path, kind, and signature.")]
    public static string SearchVbSymbols(
        [Description("Search query — symbol name or partial name (e.g., 'LoanPayment', 'Customer', 'Process')")] string query,
        [Description("Optional: filter by kind (Class, Module, Interface, Enum, Sub, Function, Property, Event, Field, WithEvents, Structure). Leave empty for all.")] string? kind = null,
        [Description("Optional: filter by access level (Public, Private, Protected, Friend). Leave empty for all.")] string? access = null,
        [Description("Maximum results to return (default 50)")] int limit = 50)
    {
        var sb = new StringBuilder();

        var sql = """
            SELECT s.id, s.kind, s.name, s.qualified_name, s.namespace, s.signature,
                   s.return_type, s.access, s.modifiers, s.line_start, s.line_end, f.path
            FROM symbols_fts fts
            JOIN symbols s ON s.id = fts.rowid
            JOIN files f ON f.id = s.file_id
            WHERE symbols_fts MATCH @query
            """;
        var parameters = new List<(string, object?)> { ("@query", EscapeFtsQuery(query)) };

        if (!string.IsNullOrEmpty(kind))
        {
            sql += " AND s.kind = @kind";
            parameters.Add(("@kind", kind));
        }
        if (!string.IsNullOrEmpty(access))
        {
            sql += " AND s.access = @access";
            parameters.Add(("@access", access));
        }

        sql += " ORDER BY rank LIMIT @limit";
        parameters.Add(("@limit", limit));

        var results = Store.Query(sql, parameters.ToArray());

        if (results.Count == 0)
        {
            return SearchVbSymbolsLike(query, kind, access, limit);
        }

        sb.AppendLine($"## VB.NET Symbol Search: \"{query}\" ({results.Count} results)");
        sb.AppendLine();
        FormatSymbolResults(sb, results);
        return sb.ToString();
    }

    private static string SearchVbSymbolsLike(string query, string? kind, string? access, int limit)
    {
        var sb = new StringBuilder();
        var sql = """
            SELECT s.id, s.kind, s.name, s.qualified_name, s.namespace, s.signature,
                   s.return_type, s.access, s.modifiers, s.line_start, s.line_end, f.path
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE (s.name LIKE @pattern OR s.qualified_name LIKE @pattern)
            """;
        var parameters = new List<(string, object?)> { ("@pattern", $"%{query}%") };

        if (!string.IsNullOrEmpty(kind))
        {
            sql += " AND s.kind = @kind";
            parameters.Add(("@kind", kind));
        }
        if (!string.IsNullOrEmpty(access))
        {
            sql += " AND s.access = @access";
            parameters.Add(("@access", access));
        }

        sql += " ORDER BY s.name LIMIT @limit";
        parameters.Add(("@limit", limit));

        var results = Store.Query(sql, parameters.ToArray());
        sb.AppendLine($"## VB.NET Symbol Search: \"{query}\" ({results.Count} results)");
        sb.AppendLine();
        FormatSymbolResults(sb, results);
        return sb.ToString();
    }

    [McpServerTool(Name = "get_vb_type"), Description(
        "Get full details for a VB.NET type (class, module, interface, enum, structure) including " +
        "all members, inheritance, and implementations. Use qualified name or simple name.")]
    public static string GetVbType(
        [Description("Type name — qualified (e.g., 'MyApp.Models.Customer') or simple (e.g., 'Customer')")] string name)
    {
        var sb = new StringBuilder();

        var results = Store.Query(
            """
            SELECT s.*, f.path FROM symbols s JOIN files f ON f.id = s.file_id
            WHERE s.qualified_name = @name AND s.kind IN ('Class','Module','Interface','Enum','Structure')
            """, ("@name", name));

        if (results.Count == 0)
        {
            results = Store.Query(
                """
                SELECT s.*, f.path FROM symbols s JOIN files f ON f.id = s.file_id
                WHERE s.name = @name AND s.kind IN ('Class','Module','Interface','Enum','Structure')
                """, ("@name", name));
        }

        if (results.Count == 0)
            return $"No type found matching '{name}'. Try search_vb_symbols for a broader search.";

        if (results.Count > 1)
        {
            sb.AppendLine($"Multiple types match '{name}'. Showing all:\n");
        }

        foreach (var type in results)
        {
            var symbolId = (long)type["id"]!;
            var qn = (string)type["qualified_name"]!;

            sb.AppendLine($"## {type["kind"]} {qn}");
            sb.AppendLine($"**File:** {type["path"]}:{type["line_start"]}");
            sb.AppendLine($"**Access:** {type["access"]} | **Modifiers:** {type["modifiers"] ?? "none"}");
            sb.AppendLine($"**Signature:** `{type["signature"]}`");
            sb.AppendLine();

            var inherits = Store.Query(
                "SELECT target_name, kind FROM edges WHERE source_id = @id AND kind IN ('Inherits','Implements')",
                ("@id", symbolId));
            if (inherits.Count > 0)
            {
                sb.AppendLine("### Inheritance");
                foreach (var edge in inherits)
                    sb.AppendLine($"- **{edge["kind"]}** {edge["target_name"]}");
                sb.AppendLine();
            }

            var members = Store.Query(
                """
                SELECT kind, name, signature, return_type, access, modifiers, line_start
                FROM symbols WHERE parent_symbol_id = @id ORDER BY line_start
                """, ("@id", symbolId));

            if (members.Count > 0)
            {
                sb.AppendLine($"### Members ({members.Count})");
                foreach (var m in members)
                {
                    var rt = m["return_type"] != null ? $" As {m["return_type"]}" : "";
                    sb.AppendLine($"- [{m["access"]}] {m["kind"]} **{m["name"]}**{rt} (line {m["line_start"]})");
                }
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "get_vb_method"), Description(
        "Get detailed information about a VB.NET method (Sub or Function) including its signature, " +
        "parameters, Handles/Implements clauses, and source file location.")]
    public static string GetVbMethod(
        [Description("Method name — qualified (e.g., 'MyForm.Page_Load') or simple (e.g., 'Page_Load'). Can also use 'ClassName.MethodName' format.")] string name)
    {
        var sb = new StringBuilder();

        var results = Store.Query(
            """
            SELECT s.*, f.path FROM symbols s JOIN files f ON f.id = s.file_id
            WHERE s.qualified_name = @name AND s.kind IN ('Sub','Function')
            """, ("@name", name));

        if (results.Count == 0)
        {
            results = Store.Query(
                """
                SELECT s.*, f.path FROM symbols s JOIN files f ON f.id = s.file_id
                WHERE s.kind IN ('Sub','Function') AND (s.name = @name OR s.qualified_name LIKE @pattern)
                ORDER BY s.qualified_name LIMIT 20
                """, ("@name", name), ("@pattern", $"%.{name}"));
        }

        if (results.Count == 0)
            return $"No method found matching '{name}'. Try search_vb_symbols with kind='Sub' or kind='Function'.";

        foreach (var method in results)
        {
            var symbolId = (long)method["id"]!;
            sb.AppendLine($"## {method["kind"]} {method["qualified_name"]}");
            sb.AppendLine($"**File:** {method["path"]}:{method["line_start"]}-{method["line_end"]}");
            sb.AppendLine($"**Access:** {method["access"]} | **Modifiers:** {method["modifiers"] ?? "none"}");
            if (method["return_type"] != null)
                sb.AppendLine($"**Returns:** {method["return_type"]}");
            sb.AppendLine($"**Signature:** `{method["signature"]}`");

            var edges = Store.Query(
                "SELECT target_name, kind FROM edges WHERE source_id = @id",
                ("@id", symbolId));
            if (edges.Count > 0)
            {
                sb.AppendLine();
                foreach (var edge in edges)
                    sb.AppendLine($"- **{edge["kind"]}:** {edge["target_name"]}");
            }

            var fullPath = Path.Combine(RootPath, ((string)method["path"]!).Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(fullPath))
            {
                var lineStart = Convert.ToInt32(method["line_start"]!) - 1;
                var lineEnd = Convert.ToInt32(method["line_end"]!);
                var lines = File.ReadLines(fullPath).Skip(lineStart).Take(Math.Min(lineEnd - lineStart, 60)).ToList();
                sb.AppendLine();
                sb.AppendLine("```vb");
                for (int i = 0; i < lines.Count; i++)
                    sb.AppendLine($"{lineStart + i + 1,5}: {lines[i]}");
                if (lineEnd - lineStart > 60)
                    sb.AppendLine($"  ... ({lineEnd - lineStart - 60} more lines)");
                sb.AppendLine("```");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "list_vb_hierarchy"), Description(
        "Show the inheritance/implementation hierarchy for a VB.NET type. " +
        "Displays what the type inherits from, what interfaces it implements, " +
        "and what other types inherit from it.")]
    public static string ListVbHierarchy(
        [Description("Type name to trace hierarchy for")] string name)
    {
        var sb = new StringBuilder();

        var types = Store.Query(
            """
            SELECT s.id, s.kind, s.name, s.qualified_name, f.path, s.line_start
            FROM symbols s JOIN files f ON f.id = s.file_id
            WHERE s.kind IN ('Class','Module','Interface','Structure') AND (s.name = @name OR s.qualified_name = @name)
            """, ("@name", name));

        if (types.Count == 0)
            return $"No type found matching '{name}'.";

        foreach (var type in types)
        {
            var symbolId = (long)type["id"]!;
            var qn = (string)type["qualified_name"]!;

            sb.AppendLine($"## Hierarchy: {type["kind"]} {qn}");
            sb.AppendLine($"**File:** {type["path"]}:{type["line_start"]}");
            sb.AppendLine();

            var parents = Store.Query(
                "SELECT target_name, kind FROM edges WHERE source_id = @id AND kind IN ('Inherits','Implements')",
                ("@id", symbolId));
            if (parents.Count > 0)
            {
                sb.AppendLine("### Bases (this type extends/implements)");
                foreach (var p in parents)
                    sb.AppendLine($"- {p["kind"]}: **{p["target_name"]}**");
                sb.AppendLine();
            }

            var children = Store.Query(
                """
                SELECT s.kind, s.name, s.qualified_name, f.path, s.line_start, e.kind as edge_kind
                FROM edges e
                JOIN symbols s ON s.id = e.source_id
                JOIN files f ON f.id = s.file_id
                WHERE (e.target_name = @name OR e.target_name = @qn)
                AND e.kind IN ('Inherits','Implements')
                ORDER BY s.qualified_name
                """, ("@name", (string)type["name"]!), ("@qn", qn));
            if (children.Count > 0)
            {
                sb.AppendLine($"### Derived Types ({children.Count} types extend/implement this)");
                foreach (var c in children)
                    sb.AppendLine($"- {c["edge_kind"]}: **{c["qualified_name"]}** ({c["kind"]}) — {c["path"]}:{c["line_start"]}");
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("### Derived Types\nNone found.");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "find_vb_references"), Description(
        "Find all references to a VB.NET symbol across the codebase. " +
        "Searches edges (Inherits, Implements, Handles) and also performs " +
        "text-based search in source files for method calls and usages.")]
    public static string FindVbReferences(
        [Description("Symbol name to find references for")] string name,
        [Description("Maximum results (default 30)")] int limit = 30)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## References to '{name}'");
        sb.AppendLine();

        var edgeRefs = Store.Query(
            """
            SELECT s.kind as source_kind, s.qualified_name as source_qn, e.kind as edge_kind,
                   f.path, s.line_start
            FROM edges e
            JOIN symbols s ON s.id = e.source_id
            JOIN files f ON f.id = s.file_id
            WHERE e.target_name LIKE @pattern
            ORDER BY e.kind, s.qualified_name
            LIMIT @limit
            """, ("@pattern", $"%{name}%"), ("@limit", limit));

        if (edgeRefs.Count > 0)
        {
            sb.AppendLine($"### Structural References ({edgeRefs.Count})");
            foreach (var r in edgeRefs)
                sb.AppendLine($"- [{r["edge_kind"]}] **{r["source_qn"]}** ({r["source_kind"]}) — {r["path"]}:{r["line_start"]}");
            sb.AppendLine();
        }

        var containingSymbols = Store.Query(
            """
            SELECT s.kind, s.qualified_name, f.path, s.line_start
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE s.qualified_name LIKE @pattern AND s.name != @name
            ORDER BY s.qualified_name
            LIMIT @limit
            """, ("@pattern", $"%{name}%"), ("@name", name));

        if (containingSymbols.Count > 0)
        {
            sb.AppendLine($"### Name Matches ({containingSymbols.Count})");
            foreach (var s in containingSymbols)
                sb.AppendLine($"- {s["kind"]} **{s["qualified_name"]}** — {s["path"]}:{s["line_start"]}");
            sb.AppendLine();
        }

        var importRefs = Store.Query(
            """
            SELECT DISTINCT i.namespace, f.path
            FROM imports i
            JOIN files f ON f.id = i.file_id
            WHERE i.namespace LIKE @pattern
            ORDER BY i.namespace
            LIMIT @limit
            """, ("@pattern", $"%{name}%"));

        if (importRefs.Count > 0)
        {
            sb.AppendLine($"### Import References ({importRefs.Count})");
            foreach (var i in importRefs)
                sb.AppendLine($"- `Imports {i["namespace"]}` in {i["path"]}");
            sb.AppendLine();
        }

        if (edgeRefs.Count == 0 && containingSymbols.Count == 0 && importRefs.Count == 0)
            sb.AppendLine("No references found in the VB.NET index. Try Grep for text-based search.");

        return sb.ToString();
    }

    [McpServerTool(Name = "search_vb_code"), Description(
        "Search VB.NET source code with structural context. Combines text search " +
        "with knowledge of which class/method contains each match. More context-aware " +
        "than plain grep.")]
    public static string SearchVbCode(
        [Description("Text pattern to search for in VB.NET source files (case-insensitive)")] string pattern,
        [Description("Optional: filter to files matching this path pattern")] string? pathFilter = null,
        [Description("Maximum results (default 30)")] int limit = 30)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## VB.NET Code Search: \"{pattern}\"");
        sb.AppendLine();

        var fileSql = "SELECT id, path FROM files";
        var fileParams = new List<(string, object?)>();
        if (!string.IsNullOrEmpty(pathFilter))
        {
            fileSql += " WHERE path LIKE @pathFilter";
            fileParams.Add(("@pathFilter", $"%{pathFilter}%"));
        }

        var files = Store.Query(fileSql, fileParams.ToArray());
        int matchCount = 0;

        foreach (var file in files)
        {
            if (matchCount >= limit) break;

            var relativePath = (string)file["path"]!;
            var fullPath = Path.Combine(RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath)) continue;

            var lines = File.ReadAllLines(fullPath);
            for (int i = 0; i < lines.Length && matchCount < limit; i++)
            {
                if (lines[i].Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    var fileId = (long)file["id"]!;
                    var lineNum = i + 1;
                    var container = Store.Query(
                        """
                        SELECT kind, qualified_name FROM symbols
                        WHERE file_id = @fid AND line_start <= @line AND line_end >= @line
                        ORDER BY (line_end - line_start) ASC LIMIT 1
                        """, ("@fid", fileId), ("@line", lineNum));

                    var ctx = container.Count > 0
                        ? $" (in {container[0]["kind"]} {container[0]["qualified_name"]})"
                        : "";

                    sb.AppendLine($"**{relativePath}:{lineNum}**{ctx}");
                    sb.AppendLine($"```vb");
                    for (int j = Math.Max(0, i - 1); j <= Math.Min(lines.Length - 1, i + 1); j++)
                    {
                        var marker = j == i ? ">" : " ";
                        sb.AppendLine($"{marker} {j + 1,5}: {lines[j]}");
                    }
                    sb.AppendLine("```");
                    sb.AppendLine();
                    matchCount++;
                }
            }
        }

        if (matchCount == 0)
            sb.AppendLine("No matches found.");
        else
            sb.AppendLine($"---\n{matchCount} matches shown (limit: {limit})");

        return sb.ToString();
    }

    [McpServerTool(Name = "reindex_vb"), Description(
        "Re-index all VB.NET files. Run this after code changes to refresh the knowledge graph.")]
    public static string ReindexVb()
    {
        var store = ServerState.Store;
        if (store == null)
            return "Error: Store not initialized. Check server configuration.";

        var indexer = new VbIndexer(store, RootPath);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = indexer.IndexAll();
        sw.Stop();

        var sb = new StringBuilder();
        sb.AppendLine("## VB.NET Re-Index Complete");
        sb.AppendLine();
        sb.AppendLine($"**Duration:** {sw.Elapsed.TotalSeconds:F1}s");
        sb.AppendLine($"**Files:** {result.IndexedFiles}/{result.TotalFiles} indexed");
        sb.AppendLine($"**Symbols:** {result.TotalSymbols:N0}");
        sb.AppendLine($"**Edges:** {result.TotalEdges:N0} ({result.ResolvedEdges} resolved)");
        sb.AppendLine();

        if (result.SymbolCounts.Count > 0)
        {
            sb.AppendLine("### Symbol Breakdown");
            foreach (var (kind, count) in result.SymbolCounts.OrderByDescending(kv => kv.Value))
                sb.AppendLine($"- {kind}: {count:N0}");
            sb.AppendLine();
        }

        if (result.EdgeCounts.Count > 0)
        {
            sb.AppendLine("### Edge Breakdown");
            foreach (var (kind, count) in result.EdgeCounts.OrderByDescending(kv => kv.Value))
                sb.AppendLine($"- {kind}: {count:N0}");
            sb.AppendLine();
        }

        if (result.Errors.Count > 0)
        {
            sb.AppendLine($"### Errors ({result.Errors.Count})");
            foreach (var err in result.Errors.Take(10))
                sb.AppendLine($"- {err}");
            if (result.Errors.Count > 10)
                sb.AppendLine($"- ... and {result.Errors.Count - 10} more");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static void FormatSymbolResults(StringBuilder sb, List<Dictionary<string, object?>> results)
    {
        if (results.Count == 0)
        {
            sb.AppendLine("No results found.");
            return;
        }

        sb.AppendLine("| Kind | Name | File | Line | Access |");
        sb.AppendLine("|------|------|------|------|--------|");
        foreach (var r in results)
        {
            sb.AppendLine($"| {r["kind"]} | **{r["name"]}** | {r["path"]}:{r["line_start"]} | {r["line_start"]} | {r["access"]} |");
        }
        sb.AppendLine();

        sb.AppendLine("### Signatures");
        foreach (var r in results.Take(20))
        {
            sb.AppendLine($"- **{r["qualified_name"]}** ({r["path"]}:{r["line_start"]})");
            sb.AppendLine($"  `{r["signature"]}`");
        }
    }

    [McpServerTool(Name = "bridge_vb_imports"), Description(
        "Show the cross-language bridge for a VB.NET file or type. Lists which C# classes, interfaces, " +
        "and enums from the configured sibling C# knowledge graph are accessible via the file's " +
        "Imports statements. Requires --bridge-config to be set at server startup. " +
        "Use this to understand VB.NET → C# dependencies.")]
    public static string BridgeVbImports(
        [Description("VB.NET file path (e.g., 'Web/loan/payment.aspx.vb') or type name (e.g., 'LoanPage')")] string target,
        [Description("Optional: filter C# results by label (Class, Interface, Enum). Leave empty for all.")] string? csharpLabel = null,
        [Description("Maximum results (default 50)")] int limit = 50)
    {
        if (ServerState.BridgeConfig?.IsUsable() != true)
            return "Cross-language bridge is not configured. Pass `--bridge-config <path>` (or set VB_BRIDGE_CONFIG) at server startup. See README.md for the schema.";

        var sb = new StringBuilder();
        List<Dictionary<string, object?>> imports;

        var fileResult = Store.Query(
            "SELECT id FROM files WHERE path LIKE @pattern LIMIT 1",
            ("@pattern", $"%{target}%"));

        if (fileResult.Count > 0)
        {
            var fileId = (long)fileResult[0]["id"]!;
            imports = Store.Query(
                "SELECT DISTINCT namespace FROM imports WHERE file_id = @fid ORDER BY namespace",
                ("@fid", fileId));
            sb.AppendLine($"## Cross-Language Bridge: {target}");
        }
        else
        {
            var typeResult = Store.Query(
                """
                SELECT DISTINCT f.id, f.path FROM symbols s
                JOIN files f ON f.id = s.file_id
                WHERE (s.name = @name OR s.qualified_name = @name)
                  AND s.kind IN ('Class','Module','Interface','Structure')
                LIMIT 5
                """, ("@name", target));

            if (typeResult.Count == 0)
                return $"No file or type found matching '{target}'.";

            var fileIds = typeResult.Select(r => (long)r["id"]!).Distinct().ToList();
            var idList = string.Join(",", fileIds);
            imports = Store.Query(
                $"SELECT DISTINCT namespace FROM imports WHERE file_id IN ({idList}) ORDER BY namespace");

            var files = string.Join(", ", typeResult.Select(r => r["path"]));
            sb.AppendLine($"## Cross-Language Bridge: {target}");
            sb.AppendLine($"**Files:** {files}");
        }

        sb.AppendLine($"**VB.NET Imports:** {imports.Count} namespaces");
        sb.AppendLine();

        if (imports.Count == 0)
        {
            sb.AppendLine("No imports found for this target.");
            return sb.ToString();
        }

        int totalMappings = 0;
        foreach (var imp in imports)
        {
            var ns = (string)imp["namespace"]!;
            var bridgeSql = """
                SELECT csharp_name, csharp_label, csharp_file_path, csharp_qualified_name
                FROM bridge_mappings
                WHERE vb_import_namespace = @ns
                """;
            var parameters = new List<(string, object?)> { ("@ns", ns) };

            if (!string.IsNullOrEmpty(csharpLabel))
            {
                bridgeSql += " AND csharp_label = @label";
                parameters.Add(("@label", csharpLabel));
            }
            bridgeSql += " ORDER BY csharp_label, csharp_name LIMIT @limit";
            parameters.Add(("@limit", limit));

            var mappings = Store.Query(bridgeSql, parameters.ToArray());
            if (mappings.Count > 0)
            {
                sb.AppendLine($"### `Imports {ns}` → {mappings.Count} C# types");
                foreach (var m in mappings)
                {
                    sb.AppendLine($"- [{m["csharp_label"]}] **{m["csharp_name"]}** — {m["csharp_file_path"]}");
                    totalMappings++;
                }
                sb.AppendLine();
            }
        }

        if (totalMappings == 0)
            sb.AppendLine("No C# graph mappings found for these imports. The sibling graph DB may need re-indexing.");
        else
            sb.AppendLine($"---\n**Total:** {totalMappings} C# types accessible from this VB.NET code");

        return sb.ToString();
    }

    [McpServerTool(Name = "trace_vb_calls"), Description(
        "Trace likely call chains from a VB.NET method or class into the bridged C# graph. " +
        "Requires --bridge-config to be set at server startup.")]
    public static string TraceVbCalls(
        [Description("VB.NET method or class name (e.g., 'LoanPage.Page_Load', 'RepoLogic.Save')")] string name,
        [Description("Maximum C# matches per symbol (default 20)")] int limit = 20)
    {
        if (ServerState.BridgeConfig?.IsUsable() != true)
            return "Cross-language bridge is not configured. Pass `--bridge-config <path>` (or set VB_BRIDGE_CONFIG) at server startup.";

        var sb = new StringBuilder();
        var symbols = Store.Query(
            """
            SELECT s.id, s.kind, s.name, s.qualified_name, s.file_id, s.line_start, s.line_end, f.path
            FROM symbols s JOIN files f ON f.id = s.file_id
            WHERE (s.qualified_name = @name OR s.name = @name OR s.qualified_name LIKE @pattern)
              AND s.kind IN ('Sub','Function','Class','Module')
            ORDER BY s.kind, s.qualified_name
            LIMIT 5
            """, ("@name", name), ("@pattern", $"%.{name}"));

        if (symbols.Count == 0)
            return $"No VB.NET symbol found matching '{name}'.";

        foreach (var sym in symbols)
        {
            var fileId = (long)sym["file_id"]!;
            var filePath = (string)sym["path"]!;
            var lineStart = Convert.ToInt32(sym["line_start"]!);
            var lineEnd = Convert.ToInt32(sym["line_end"]!);

            sb.AppendLine($"## {sym["kind"]} {sym["qualified_name"]}");
            sb.AppendLine($"**File:** {filePath}:{lineStart}-{lineEnd}");
            sb.AppendLine();

            var bridgedImports = Store.Query(
                """
                SELECT DISTINCT i.namespace FROM imports i
                INNER JOIN bridge_mappings b ON b.vb_import_namespace = i.namespace
                WHERE i.file_id = @fid
                ORDER BY i.namespace
                """, ("@fid", fileId));

            if (bridgedImports.Count == 0)
            {
                sb.AppendLine("No C# bridge imports found for this file.");
                sb.AppendLine();
                continue;
            }

            var fullPath = Path.Combine(RootPath, filePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                sb.AppendLine("Source file not found on disk.");
                sb.AppendLine();
                continue;
            }

            var lines = File.ReadLines(fullPath)
                .Skip(lineStart - 1)
                .Take(Math.Min(lineEnd - lineStart + 1, 200))
                .ToList();
            var sourceBlock = string.Join("\n", lines);

            sb.AppendLine("### Cross-Language Calls");
            int callsFound = 0;

            foreach (var imp in bridgedImports)
            {
                var ns = (string)imp["namespace"]!;
                var csharpTypes = Store.Query(
                    """
                    SELECT DISTINCT csharp_name, csharp_label, csharp_file_path, csharp_qualified_name
                    FROM bridge_mappings
                    WHERE vb_import_namespace = @ns AND csharp_label IN ('Class','Interface','Enum')
                    ORDER BY csharp_name
                    """, ("@ns", ns));

                foreach (var cType in csharpTypes)
                {
                    var cName = (string)cType["csharp_name"]!;
                    if (cName.Length >= 3 && sourceBlock.Contains(cName, StringComparison.OrdinalIgnoreCase))
                    {
                        sb.AppendLine($"- **{cName}** [{cType["csharp_label"]}] from `Imports {ns}`");
                        sb.AppendLine($"  C# source: {cType["csharp_file_path"]}");
                        sb.AppendLine($"  Graph QN: `{cType["csharp_qualified_name"]}`");
                        callsFound++;
                        if (callsFound >= limit) break;
                    }
                }
                if (callsFound >= limit) break;
            }

            if (callsFound == 0)
                sb.AppendLine("No C# type references detected in the source code of this symbol.");
            else
                sb.AppendLine($"\n**{callsFound} cross-language references found.**");

            sb.AppendLine();
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "rebuild_vb_bridge"), Description(
        "Rebuild the cross-language bridge mappings against the configured sibling C# graph. " +
        "Run after the sibling graph DB is re-indexed. No-op if --bridge-config is not set.")]
    public static string RebuildVbBridge()
    {
        var cfg = ServerState.BridgeConfig;
        if (cfg?.IsUsable() != true)
            return "Bridge not configured — start the server with --bridge-config <path>.";

        var bridge = new CSharpBridge(Store, cfg);
        var r = bridge.BuildBridge();
        if (r.Error != null) return $"Bridge error: {r.Error}";
        return $"## Bridge Rebuilt\n\n- Namespaces: {r.DistinctImports}\n- Mappings: {r.MappingsCreated:N0}\n- Files with bridge: {r.FilesWithBridge:N0}";
    }

    private static string EscapeFtsQuery(string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 1)
        {
            var clean = words[0].Replace("\"", "");
            return $"\"{clean}\" OR \"{clean}\"*";
        }

        return string.Join(" OR ", words.Select(w => $"\"{w.Replace("\"", "")}\""));
    }
}

/// <summary>Shared state between MCP tools and the server host.</summary>
public static class ServerState
{
    public static SqliteStore? Store { get; set; }
    public static string RootPath { get; set; } = "";
    public static BridgeConfig? BridgeConfig { get; set; }
}
