using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using VbKnowledgeGraph;

// ---------------------------------------------------------------------------
// CLI argument parsing — accepts:
//   --repo-path <path>      Root directory to scan for VB.NET files
//   --db-path <path>        SQLite index DB path (default: ~/.cache/vb-knowledge-graph/vb-index.db)
//   --bridge-config <path>  Optional cross-language bridge config (JSON). See BridgeConfig.cs.
//   --reindex               Force a full re-index on startup (clears existing index)
// Env-var fallbacks: VB_ROOT_PATH, VB_DB_PATH, VB_BRIDGE_CONFIG
// ---------------------------------------------------------------------------

string? cliRepoPath = null;
string? cliDbPath = null;
string? cliBridgeConfig = null;
bool forceReindex = false;
var passthroughArgs = new List<string>();

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--repo-path" when i + 1 < args.Length:
            cliRepoPath = args[++i]; break;
        case "--db-path" when i + 1 < args.Length:
            cliDbPath = args[++i]; break;
        case "--bridge-config" when i + 1 < args.Length:
            cliBridgeConfig = args[++i]; break;
        case "--reindex":
            forceReindex = true; break;
        default:
            passthroughArgs.Add(args[i]); break;
    }
}

// Resolve root path: CLI flag > env var > current working directory
var rootPath = cliRepoPath
    ?? Environment.GetEnvironmentVariable("VB_ROOT_PATH")
    ?? Environment.CurrentDirectory;
rootPath = Path.GetFullPath(rootPath);

if (!Directory.Exists(rootPath))
{
    Console.Error.WriteLine($"[vb-knowledge-graph] Repo path does not exist: {rootPath}");
    return 1;
}

// Resolve DB path: CLI flag > env var > default user cache
var dbPath = cliDbPath
    ?? Environment.GetEnvironmentVariable("VB_DB_PATH")
    ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cache", "vb-knowledge-graph", "vb-index.db");

Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

var store = new SqliteStore(dbPath);
ServerState.Store = store;
ServerState.RootPath = rootPath;

// Resolve bridge config: CLI flag > env var > none.
var bridgeConfigPath = cliBridgeConfig ?? Environment.GetEnvironmentVariable("VB_BRIDGE_CONFIG");
BridgeConfig? bridgeConfig = null;
if (!string.IsNullOrWhiteSpace(bridgeConfigPath))
{
    try
    {
        bridgeConfig = BridgeConfig.LoadFromFile(bridgeConfigPath);
        if (bridgeConfig == null)
            Console.Error.WriteLine($"[vb-knowledge-graph] bridge config not found at: {bridgeConfigPath} (bridge disabled)");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[vb-knowledge-graph] failed to load bridge config: {ex.Message} (bridge disabled)");
        bridgeConfig = null;
    }
}
ServerState.BridgeConfig = bridgeConfig;

Console.Error.WriteLine($"[vb-knowledge-graph] root: {rootPath}");
Console.Error.WriteLine($"[vb-knowledge-graph] db:   {dbPath}");
Console.Error.WriteLine($"[vb-knowledge-graph] bridge: {(bridgeConfig?.IsUsable() == true ? $"enabled ({bridgeConfig.CmmProjectKey})" : "disabled")}");

// Index if empty or --reindex was requested
var fileCount = store.Query("SELECT COUNT(*) as cnt FROM files");
var needsIndex = forceReindex || fileCount.Count == 0 || Convert.ToInt32(fileCount[0]["cnt"]!) == 0;

if (needsIndex)
{
    var why = forceReindex ? "--reindex requested" : "first run";
    Console.Error.WriteLine($"[vb-knowledge-graph] {why} — indexing VB.NET files from: {rootPath}");
    var indexer = new VbIndexer(store, rootPath);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var result = indexer.IndexAll();
    sw.Stop();
    Console.Error.WriteLine($"[vb-knowledge-graph] Indexed {result.IndexedFiles} files, {result.TotalSymbols} symbols, {result.TotalEdges} edges in {sw.Elapsed.TotalSeconds:F1}s");
    if (result.Errors.Count > 0)
        Console.Error.WriteLine($"[vb-knowledge-graph] {result.Errors.Count} parse errors (non-fatal)");
}
else
{
    var count = Convert.ToInt32(fileCount[0]["cnt"]!);
    Console.Error.WriteLine($"[vb-knowledge-graph] Loaded existing index: {count} files");
}

// Build cross-language bridge if configured.
if (bridgeConfig?.IsUsable() == true)
{
    try
    {
        var bridge = new CSharpBridge(store, bridgeConfig);
        var br = bridge.BuildBridge();
        if (br.Error != null)
            Console.Error.WriteLine($"[vb-knowledge-graph] bridge warning: {br.Error}");
        else
            Console.Error.WriteLine($"[vb-knowledge-graph] bridge: {br.MappingsCreated} mappings across {br.FilesWithBridge} files ({br.DistinctImports} namespaces)");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[vb-knowledge-graph] bridge build failed (non-fatal): {ex.Message}");
    }
}

// Build and run the MCP server (stdio transport)
var builder = Host.CreateApplicationBuilder(passthroughArgs.ToArray());
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "vb-knowledge-graph",
            Version = "0.2.0"
        };
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
return 0;
