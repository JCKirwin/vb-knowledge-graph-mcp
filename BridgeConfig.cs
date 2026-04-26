using System.Text.Json;
using System.Text.Json.Serialization;

namespace VbKnowledgeGraph;

/// <summary>
/// Optional configuration for the cross-language bridge to a sibling C# knowledge graph
/// (e.g. codebase-memory-mcp). The bridge is OFF unless a config is loaded and Enabled=true.
///
/// Example JSON:
/// <code>
/// {
///   "enabled": true,
///   "cmmDbPath": "C:/Users/me/.cache/codebase-memory-mcp/MyProject.db",
///   "cmmProjectKey": "C-Projects-MyProject",
///   "namespaceFilters": [ "MyCompany.%", "MyApp.%" ],
///   "csharpLabels": [ "Class", "Interface", "Enum" ]
/// }
/// </code>
/// </summary>
public sealed class BridgeConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Path to the sibling C# knowledge-graph SQLite database. If null, the bridge is disabled.
    /// </summary>
    [JsonPropertyName("cmmDbPath")]
    public string? CmmDbPath { get; set; }

    /// <summary>
    /// Project key in the sibling DB's <c>nodes.project</c> column. Required.
    /// </summary>
    [JsonPropertyName("cmmProjectKey")]
    public string? CmmProjectKey { get; set; }

    /// <summary>
    /// SQL LIKE patterns that select which VB.NET <c>Imports</c> namespaces to bridge.
    /// Defaults to a single wildcard (everything). Use this to skip framework imports
    /// (System.*, Microsoft.*) that would never have local C# nodes.
    /// </summary>
    [JsonPropertyName("namespaceFilters")]
    public List<string> NamespaceFilters { get; set; } = new() { "%" };

    /// <summary>
    /// Which C# node labels to map. Defaults to types only.
    /// </summary>
    [JsonPropertyName("csharpLabels")]
    public List<string> CsharpLabels { get; set; } = new() { "Class", "Interface", "Enum" };

    public static BridgeConfig? LoadFromFile(string path)
    {
        if (!File.Exists(path)) return null;
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<BridgeConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });
    }

    /// <summary>True if the config has the minimum data to attempt a bridge build.</summary>
    public bool IsUsable() =>
        Enabled
        && !string.IsNullOrWhiteSpace(CmmDbPath)
        && !string.IsNullOrWhiteSpace(CmmProjectKey);
}
