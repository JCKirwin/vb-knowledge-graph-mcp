# vb-knowledge-graph-mcp

> **MCP server for navigating VB.NET codebases via a Roslyn-backed knowledge graph.**

A Model Context Protocol (MCP) server that indexes a VB.NET codebase using Roslyn, persists the graph in SQLite, and exposes tools that let an AI agent search symbols, walk inheritance hierarchies, and find references — all without forcing the agent to grep through thousands of `.vb` files.

[![build](https://github.com/JCKirwin/vb-knowledge-graph-mcp/actions/workflows/build.yml/badge.svg)](https://github.com/JCKirwin/vb-knowledge-graph-mcp/actions/workflows/build.yml)

---

## Why

Modern AI coding assistants are built around C-family languages. When they meet a large VB.NET project — common in legacy enterprise systems — they fall back to regex grep, miss inheritance edges, and burn tokens reading whole files just to confirm a method signature.

This MCP server gives the agent a structural view: "find every class that inherits from `Page`" or "show me the signature and Handles clauses of `SaveCustomer`" return precise answers in milliseconds.

---

## What it indexes

| Symbol kinds | Edges |
|---|---|
| Class, Module, Interface, Enum, Structure | Inherits |
| Sub, Function, Property, Event, Field | Implements |
| WithEvents fields, nested types | Handles |
| Namespaces & Imports | (Imports tracked separately) |

Symbol kinds are searchable individually, edges are queryable both directions (bases + derived types).

---

## Installation

### Prerequisites
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- An MCP-aware client (e.g., [Claude Code](https://claude.com/claude-code))

### Build from source
```bash
git clone https://github.com/JCKirwin/vb-knowledge-graph-mcp.git
cd vb-knowledge-graph-mcp
dotnet build -c Release
```
The binary lands at `bin/Release/net10.0/vb-knowledge-graph.dll`.

---

## Configuration

### Register with Claude Code (`.mcp.json`)
```json
{
  "mcpServers": {
    "vb-knowledge-graph": {
      "command": "dotnet",
      "args": [
        "C:/path/to/vb-knowledge-graph-mcp/bin/Release/net10.0/vb-knowledge-graph.dll",
        "--repo-path", "C:/path/to/your/vb-project"
      ]
    }
  }
}
```

### CLI flags
| Flag | Description | Fallback |
|---|---|---|
| `--repo-path <path>` | Root directory to scan recursively for `*.vb` files | `$VB_ROOT_PATH` env var, then current working directory |
| `--db-path <path>` | SQLite index location | `$VB_DB_PATH` env var, then `~/.cache/vb-knowledge-graph/vb-index.db` |
| `--reindex` | Force full re-index on startup (clears existing index) | — |

The first run auto-indexes; subsequent runs reuse the cached DB. Use `--reindex` after large refactors, or call the `reindex_vb` MCP tool from your agent session.

---

## MCP Tools

| Tool | Purpose |
|---|---|
| `search_vb_symbols` | FTS5 + LIKE search by name, optional filter by kind/access |
| `get_vb_type` | Full details for a Class/Module/Interface/Enum/Structure: members, inheritance, location |
| `get_vb_method` | Method signature, Handles/Implements clauses, source snippet |
| `list_vb_hierarchy` | Bases (extends/implements) and derived types for a given type |
| `find_vb_references` | Structural references via edges + name matches + import references |
| `search_vb_code` | Text search with structural context (which class/method contains the match) |
| `reindex_vb` | Trigger a full re-index from inside the agent session |

---

## Usage examples

### 1. "Find every class that inherits from `Page`"
```
search_vb_symbols(query="Page", kind="Class")
list_vb_hierarchy(name="Page")
```
The hierarchy tool returns a list of derived types with file paths and line numbers.

### 2. "What does the `SaveCustomer` Sub do?"
```
get_vb_method(name="SaveCustomer")
```
Returns the signature, Handles/Implements clauses, and an inline source snippet (up to 60 lines).

### 3. "Where is `LoanStatus.Active` referenced?"
```
find_vb_references(name="LoanStatus.Active")
```
Returns structural edges + name matches across the indexed graph.

---

## How it works

1. **Indexer** (Roslyn `Microsoft.CodeAnalysis.VisualBasic`) walks all `*.vb` files under the repo root, parses each as a syntax tree, and extracts symbols + edges.
2. **Storage** (SQLite) persists the graph with FTS5 full-text search on symbol names and signatures.
3. **MCP server** (stdio transport via [`ModelContextProtocol`](https://www.nuget.org/packages/ModelContextProtocol)) exposes the seven tools above.
4. **Edge resolution** runs after indexing — `target_name` strings are linked to `target_id` foreign keys where qualified-name matches exist.
5. **Incremental re-index** (via `reindex_vb`) compares SHA-256 hashes per file and skips unchanged ones.

Skipped during scan: `*.designer.vb`, `AssemblyInfo.vb`, `Reference.vb`, `My Project/`, `bin/`, `obj/`.

---

## Performance

For a ~3000-file VB.NET codebase: full index in ~60-90 seconds, incremental re-index in ~5 seconds, symbol queries return in <50 ms.

---

## Roadmap

- **v0.2** — Opt-in cross-language bridge: configurable mapping from VB.NET imports to a sister language's symbol graph (e.g., a C# graph stored in another SQLite DB). Originally extracted from a coupled implementation; reintroducing it as a clean, configurable feature.
- **NuGet `dotnet tool` package** — `dotnet tool install --global vb-knowledge-graph` for one-line installs.
- **Test suite** — smoke tests covering indexer + each MCP tool against a fixture VB project.

Issues and PRs welcome.

---

## License

MIT — see [LICENSE](LICENSE).
