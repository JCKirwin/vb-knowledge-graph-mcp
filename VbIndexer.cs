using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace VbKnowledgeGraph;

/// <summary>
/// Indexes VB.NET source files using Roslyn syntax-only parsing.
/// Extracts classes, modules, interfaces, enums, structures, methods, properties,
/// events, fields, and relationship edges (Inherits, Implements, Handles, Imports).
/// </summary>
public sealed class VbIndexer
{
    private readonly SqliteStore _store;
    private readonly string _rootPath;

    // Files to skip
    private static readonly HashSet<string> SkipPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        "AssemblyInfo.vb",
        "Reference.vb",
    };

    public VbIndexer(SqliteStore store, string rootPath)
    {
        _store = store;
        _rootPath = rootPath;
    }

    public IndexResult IndexAll(bool incremental = false)
    {
        var result = new IndexResult();
        var files = ScanFiles();
        result.TotalFiles = files.Count;

        if (!incremental)
        {
            _store.ClearAll();
        }
        else
        {
            var existingRelPaths = new HashSet<string>(
                files.Select(f => Path.GetRelativePath(_rootPath, f).Replace('\\', '/')),
                StringComparer.OrdinalIgnoreCase);
            result.PurgedFiles = _store.PurgeDeletedFiles(existingRelPaths);
        }

        _store.BeginTransaction();

        try
        {
            foreach (var filePath in files)
            {
                try
                {
                    var relativePath = Path.GetRelativePath(_rootPath, filePath).Replace('\\', '/');

                    if (incremental)
                    {
                        var currentHash = ComputeFileHash(filePath);
                        var storedHash = _store.GetFileHash(relativePath);

                        if (storedHash != null && storedHash == currentHash)
                        {
                            result.SkippedFiles++;
                            continue;
                        }

                        var existing = _store.Query(
                            "SELECT id FROM files WHERE path = @p", ("@p", relativePath));
                        if (existing.Count > 0)
                            _store.DeleteFileData((long)existing[0]["id"]!);
                    }

                    IndexFile(filePath);
                    result.IndexedFiles++;
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"{filePath}: {ex.Message}");
                }
            }

            var resolved = _store.ResolveEdges();
            result.ResolvedEdges = resolved;

            _store.CommitTransaction();
            _store.RebuildFts();
        }
        catch
        {
            try { _store.Connection.CreateCommand().Let(c => { c.CommandText = "ROLLBACK"; c.ExecuteNonQuery(); }); }
            catch { /* swallow rollback errors */ }
            throw;
        }

        var stats = _store.Query("SELECT kind, COUNT(*) as cnt FROM symbols GROUP BY kind");
        foreach (var row in stats)
            result.SymbolCounts[(string)row["kind"]!] = Convert.ToInt32(row["cnt"]!);

        var edgeStats = _store.Query("SELECT kind, COUNT(*) as cnt FROM edges GROUP BY kind");
        foreach (var row in edgeStats)
            result.EdgeCounts[(string)row["kind"]!] = Convert.ToInt32(row["cnt"]!);

        return result;
    }

    private static string ComputeFileHash(string filePath)
    {
        var bytes = File.ReadAllBytes(filePath);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private List<string> ScanFiles()
    {
        var files = new List<string>();

        foreach (var file in Directory.EnumerateFiles(_rootPath, "*.vb", SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileName(file);

            if (fileName.EndsWith(".designer.vb", StringComparison.OrdinalIgnoreCase))
                continue;
            if (SkipPatterns.Contains(fileName))
                continue;
            if (file.Contains(Path.Combine("My Project", ""), StringComparison.OrdinalIgnoreCase))
                continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                continue;

            files.Add(file);
        }

        return files;
    }

    private void IndexFile(string filePath)
    {
        var source = File.ReadAllText(filePath);
        var tree = VisualBasicSyntaxTree.ParseText(source, path: filePath);
        var root = tree.GetCompilationUnitRoot();

        var relativePath = Path.GetRelativePath(_rootPath, filePath).Replace('\\', '/');
        var fileHash = ComputeFileHash(filePath);
        var fileSize = new FileInfo(filePath).Length;
        var fileId = _store.InsertFile(relativePath, DateTime.UtcNow.ToString("o"), fileHash, fileSize);

        foreach (var imp in root.Imports)
        {
            foreach (var clause in imp.ImportsClauses.OfType<SimpleImportsClauseSyntax>())
            {
                var ns = clause.Name.ToString();
                _store.InsertImport(fileId, ns);
            }
        }

        var currentNamespace = "";
        var nsBlock = root.DescendantNodes().OfType<NamespaceBlockSyntax>().FirstOrDefault();
        if (nsBlock != null)
            currentNamespace = nsBlock.NamespaceStatement.Name.ToString();

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case ClassBlockSyntax cls:
                    IndexTypeBlock(fileId, cls, cls.ClassStatement, "Class", currentNamespace, null);
                    break;
                case ModuleBlockSyntax mod:
                    IndexTypeBlock(fileId, mod, mod.ModuleStatement, "Module", currentNamespace, null);
                    break;
                case InterfaceBlockSyntax iface:
                    IndexTypeBlock(fileId, iface, iface.InterfaceStatement, "Interface", currentNamespace, null);
                    break;
                case EnumBlockSyntax en:
                    IndexEnum(fileId, en, currentNamespace);
                    break;
                case StructureBlockSyntax st:
                    IndexTypeBlock(fileId, st, st.StructureStatement, "Structure", currentNamespace, null);
                    break;
            }
        }
    }

    private void IndexTypeBlock(long fileId, SyntaxNode block, TypeStatementSyntax statement,
        string kind, string ns, long? parentId)
    {
        var name = statement.Identifier.Text;
        var qn = string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
        var access = GetAccessibility(statement.Modifiers);
        var modifiers = GetModifiers(statement.Modifiers);
        var lineSpan = block.GetLocation().GetLineSpan();
        var signature = statement.ToString().Trim();

        var symbolId = _store.InsertSymbol(fileId, kind, name, qn, ns, signature, null,
            access, modifiers, lineSpan.StartLinePosition.Line + 1, lineSpan.EndLinePosition.Line + 1, parentId);

        foreach (var child in block.ChildNodes())
        {
            if (child is InheritsStatementSyntax inherits)
            {
                foreach (var baseType in inherits.Types)
                    _store.InsertEdge(symbolId, baseType.ToString(), "Inherits");
            }
            else if (child is ImplementsStatementSyntax implements)
            {
                foreach (var iface in implements.Types)
                    _store.InsertEdge(symbolId, iface.ToString(), "Implements");
            }
        }

        foreach (var member in block.ChildNodes())
        {
            switch (member)
            {
                case MethodBlockSyntax method:
                    IndexMethod(fileId, method, qn, symbolId);
                    break;
                case PropertyBlockSyntax prop:
                    IndexProperty(fileId, prop, qn, symbolId);
                    break;
                case FieldDeclarationSyntax field:
                    IndexField(fileId, field, qn, symbolId);
                    break;
                case EventStatementSyntax evt:
                    IndexEvent(fileId, evt, qn, symbolId);
                    break;
                case ClassBlockSyntax nested:
                    IndexTypeBlock(fileId, nested, nested.ClassStatement, "Class", qn, symbolId);
                    break;
                case ModuleBlockSyntax nested:
                    IndexTypeBlock(fileId, nested, nested.ModuleStatement, "Module", qn, symbolId);
                    break;
                case InterfaceBlockSyntax nested:
                    IndexTypeBlock(fileId, nested, nested.InterfaceStatement, "Interface", qn, symbolId);
                    break;
                case StructureBlockSyntax nested:
                    IndexTypeBlock(fileId, nested, nested.StructureStatement, "Structure", qn, symbolId);
                    break;
            }
        }
    }

    private void IndexMethod(long fileId, MethodBlockSyntax method, string parentQn, long parentId)
    {
        var stmt = method.SubOrFunctionStatement;
        var name = stmt.Identifier.Text;
        var qn = $"{parentQn}.{name}";
        var kind = method.SubOrFunctionStatement.DeclarationKeyword.IsKind(SyntaxKind.SubKeyword) ? "Sub" : "Function";
        var access = GetAccessibility(stmt.Modifiers);
        var modifiers = GetModifiers(stmt.Modifiers);
        var returnType = (stmt as MethodStatementSyntax)?.AsClause?.Type.ToString();
        var lineSpan = method.GetLocation().GetLineSpan();
        var signature = stmt.ToString().Trim();

        var symbolId = _store.InsertSymbol(fileId, kind, name, qn, null, signature, returnType,
            access, modifiers, lineSpan.StartLinePosition.Line + 1, lineSpan.EndLinePosition.Line + 1, parentId);

        if (stmt.HandlesClause != null)
        {
            foreach (var evt in stmt.HandlesClause.Events)
                _store.InsertEdge(symbolId, evt.ToString(), "Handles");
        }

        if (stmt.ImplementsClause != null)
        {
            foreach (var impl in stmt.ImplementsClause.InterfaceMembers)
                _store.InsertEdge(symbolId, impl.ToString(), "Implements");
        }
    }

    private void IndexProperty(long fileId, PropertyBlockSyntax prop, string parentQn, long parentId)
    {
        var stmt = prop.PropertyStatement;
        var name = stmt.Identifier.Text;
        var qn = $"{parentQn}.{name}";
        var access = GetAccessibility(stmt.Modifiers);
        var modifiers = GetModifiers(stmt.Modifiers);
        var returnType = (stmt.AsClause as SimpleAsClauseSyntax)?.Type?.ToString();
        var lineSpan = prop.GetLocation().GetLineSpan();
        var signature = stmt.ToString().Trim();

        _store.InsertSymbol(fileId, "Property", name, qn, null, signature, returnType,
            access, modifiers, lineSpan.StartLinePosition.Line + 1, lineSpan.EndLinePosition.Line + 1, parentId);
    }

    private void IndexField(long fileId, FieldDeclarationSyntax field, string parentQn, long parentId)
    {
        foreach (var declarator in field.Declarators)
        {
            foreach (var name in declarator.Names)
            {
                var fieldName = name.Identifier.Text;
                var qn = $"{parentQn}.{fieldName}";
                var access = GetAccessibility(field.Modifiers);
                var modifiers = GetModifiers(field.Modifiers);
                var returnType = (declarator.AsClause as SimpleAsClauseSyntax)?.Type?.ToString();
                var isWithEvents = field.Modifiers.Any(m => m.IsKind(SyntaxKind.WithEventsKeyword));
                var lineSpan = field.GetLocation().GetLineSpan();
                var signature = field.ToString().Trim();

                var kind = isWithEvents ? "WithEvents" : "Field";
                _store.InsertSymbol(fileId, kind, fieldName, qn, null, signature, returnType,
                    access, modifiers, lineSpan.StartLinePosition.Line + 1, lineSpan.EndLinePosition.Line + 1, parentId);
            }
        }
    }

    private void IndexEvent(long fileId, EventStatementSyntax evt, string parentQn, long parentId)
    {
        var name = evt.Identifier.Text;
        var qn = $"{parentQn}.{name}";
        var access = GetAccessibility(evt.Modifiers);
        var lineSpan = evt.GetLocation().GetLineSpan();
        var signature = evt.ToString().Trim();

        _store.InsertSymbol(fileId, "Event", name, qn, null, signature, null,
            access, null, lineSpan.StartLinePosition.Line + 1, lineSpan.EndLinePosition.Line + 1, parentId);
    }

    private void IndexEnum(long fileId, EnumBlockSyntax en, string ns)
    {
        var name = en.EnumStatement.Identifier.Text;
        var qn = string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
        var access = GetAccessibility(en.EnumStatement.Modifiers);
        var lineSpan = en.GetLocation().GetLineSpan();
        var signature = en.EnumStatement.ToString().Trim();

        _store.InsertSymbol(fileId, "Enum", name, qn, ns, signature, null,
            access, null, lineSpan.StartLinePosition.Line + 1, lineSpan.EndLinePosition.Line + 1, null);
    }

    private static string GetAccessibility(SyntaxTokenList modifiers)
    {
        if (modifiers.Any(SyntaxKind.PublicKeyword)) return "Public";
        if (modifiers.Any(SyntaxKind.PrivateKeyword)) return "Private";
        if (modifiers.Any(SyntaxKind.ProtectedKeyword)) return "Protected";
        if (modifiers.Any(SyntaxKind.FriendKeyword)) return "Friend";
        return "Private";
    }

    private static string? GetModifiers(SyntaxTokenList modifiers)
    {
        var mods = new List<string>();
        if (modifiers.Any(SyntaxKind.SharedKeyword)) mods.Add("Shared");
        if (modifiers.Any(SyntaxKind.MustInheritKeyword)) mods.Add("MustInherit");
        if (modifiers.Any(SyntaxKind.MustOverrideKeyword)) mods.Add("MustOverride");
        if (modifiers.Any(SyntaxKind.OverridesKeyword)) mods.Add("Overrides");
        if (modifiers.Any(SyntaxKind.OverridableKeyword)) mods.Add("Overridable");
        if (modifiers.Any(SyntaxKind.NotOverridableKeyword)) mods.Add("NotOverridable");
        if (modifiers.Any(SyntaxKind.OverloadsKeyword)) mods.Add("Overloads");
        if (modifiers.Any(SyntaxKind.WithEventsKeyword)) mods.Add("WithEvents");
        if (modifiers.Any(SyntaxKind.ReadOnlyKeyword)) mods.Add("ReadOnly");
        if (modifiers.Any(SyntaxKind.WriteOnlyKeyword)) mods.Add("WriteOnly");
        return mods.Count > 0 ? string.Join(", ", mods) : null;
    }
}

public sealed class IndexResult
{
    public int TotalFiles { get; set; }
    public int IndexedFiles { get; set; }
    public int SkippedFiles { get; set; }
    public int PurgedFiles { get; set; }
    public int ResolvedEdges { get; set; }
    public Dictionary<string, int> SymbolCounts { get; } = new();
    public Dictionary<string, int> EdgeCounts { get; } = new();
    public List<string> Errors { get; } = new();

    public int TotalSymbols => SymbolCounts.Values.Sum();
    public int TotalEdges => EdgeCounts.Values.Sum();
}
