using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using thuvu.Models;
using thuvu.Tools.Parsers;

namespace thuvu.Tools
{
    /// <summary>
    /// Code indexer that parses source files and extracts symbols.
    /// Uses Roslyn for C# and regex-based parsers for other languages.
    /// Supports incremental indexing based on file hashes.
    /// </summary>
    public class CodeIndexer
    {
        private readonly SqliteService _db;
        
        // Language-specific parsers
        private static readonly TypeScriptParser _tsParser = new();
        private static readonly PythonParser _pyParser = new();
        private static readonly GoParser _goParser = new();

        public CodeIndexer()
        {
            _db = SqliteService.Instance;
        }

        /// <summary>
        /// Index a directory recursively.
        /// </summary>
        public async Task<IndexResult> IndexDirectoryAsync(string directory, bool force = false, 
            CancellationToken ct = default)
        {
            var result = new IndexResult();
            var config = SqliteConfig.Instance;

            if (!Directory.Exists(directory))
            {
                result.Errors.Add($"Directory not found: {directory}");
                return result;
            }

            var files = GetIndexableFiles(directory, config);
            result.TotalFiles = files.Count;

            foreach (var file in files)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    var indexed = await IndexFileAsync(file, force, ct).ConfigureAwait(false);
                    if (indexed)
                        result.IndexedFiles++;
                    else
                        result.SkippedFiles++;
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"{file}: {ex.Message}");
                }
            }

            return result;
        }

        /// <summary>
        /// Index a single file.
        /// </summary>
        public async Task<bool> IndexFileAsync(string filePath, bool force = false, CancellationToken ct = default)
        {
            var config = SqliteConfig.Instance;
            var fullPath = Path.GetFullPath(filePath);

            if (!File.Exists(fullPath))
                return false;

            var fileInfo = new FileInfo(fullPath);
            if (fileInfo.Length > config.MaxFileSizeBytes)
            {
                AgentLogger.LogWarning("Skipping large file: {Path} ({Size} bytes)", fullPath, fileInfo.Length);
                return false;
            }

            // Check if file needs reindexing
            var hash = ComputeFileHash(fullPath);
            var existingMeta = await _db.GetFileMetadataAsync(fullPath, ct).ConfigureAwait(false);

            if (!force && existingMeta != null && existingMeta.Hash == hash)
            {
                // File unchanged, skip
                return false;
            }

            // Parse and index based on extension
            var ext = Path.GetExtension(fullPath).ToLowerInvariant();
            var symbols = await ParseFileAsync(fullPath, ext, ct).ConfigureAwait(false);

            // Set file path on all symbols
            foreach (var symbol in symbols)
                symbol.FilePath = fullPath;

            // Batch: delete + insert all symbols + upsert file metadata in one transaction
            await _db.IndexFileBatchAsync(fullPath, hash, fileInfo.Length, symbols, ct).ConfigureAwait(false);

            AgentLogger.LogInfo("Indexed {Path}: {Count} symbols", fullPath, symbols.Count);
            return true;
        }

        /// <summary>
        /// Parse a file using the appropriate parser based on extension.
        /// </summary>
        private async Task<List<CodeSymbol>> ParseFileAsync(string filePath, string ext, CancellationToken ct)
        {
            try
            {
                return ext switch
                {
                    // C# - Roslyn parser (most accurate)
                    ".cs" => await ParseCSharpFileAsync(filePath, ct).ConfigureAwait(false),
                    
                    // TypeScript/JavaScript - Regex parser
                    ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".mts" 
                        => await _tsParser.ParseAsync(filePath, ct).ConfigureAwait(false),
                    
                    // Python - Regex parser
                    ".py" or ".pyw" or ".pyi" 
                        => await _pyParser.ParseAsync(filePath, ct).ConfigureAwait(false),
                    
                    // Go - Regex parser
                    ".go" => await _goParser.ParseAsync(filePath, ct).ConfigureAwait(false),
                    
                    // Unsupported extension - return empty list
                    _ => new List<CodeSymbol>()
                };
            }
            catch (Exception ex)
            {
                AgentLogger.LogWarning("Failed to parse {Path}: {Error}", filePath, ex.Message);
                return new List<CodeSymbol>();
            }
        }

        /// <summary>
        /// Parse a C# file using Roslyn and extract symbols.
        /// </summary>
        private async Task<List<CodeSymbol>> ParseCSharpFileAsync(string filePath, CancellationToken ct)
        {
            var symbols = new List<CodeSymbol>();
            var code = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
            var tree = CSharpSyntaxTree.ParseText(code, cancellationToken: ct);
            var root = await tree.GetRootAsync(ct).ConfigureAwait(false);

            // Extract namespace, class, interface, struct, enum declarations
            var typeDeclarations = root.DescendantNodes()
                .Where(n => n is TypeDeclarationSyntax || n is EnumDeclarationSyntax || n is DelegateDeclarationSyntax);

            foreach (var node in typeDeclarations)
            {
                var typeSymbol = ExtractTypeSymbol(node, filePath);
                if (typeSymbol != null)
                {
                    symbols.Add(typeSymbol);

                    // Extract members
                    if (node is TypeDeclarationSyntax typeDecl)
                    {
                        var memberSymbols = ExtractMembers(typeDecl, filePath, typeSymbol.Name);
                        symbols.AddRange(memberSymbols);
                    }
                    else if (node is EnumDeclarationSyntax enumDecl)
                    {
                        var enumMembers = ExtractEnumMembers(enumDecl, filePath, typeSymbol.Name);
                        symbols.AddRange(enumMembers);
                    }
                }
            }

            // Extract top-level statements/methods (C# 9+)
            var topLevelMethods = root.DescendantNodes()
                .OfType<GlobalStatementSyntax>()
                .SelectMany(g => g.DescendantNodes().OfType<LocalFunctionStatementSyntax>());

            foreach (var method in topLevelMethods)
            {
                symbols.Add(new CodeSymbol
                {
                    Name = method.Identifier.Text,
                    Kind = "function",
                    FilePath = filePath,
                    LineStart = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    LineEnd = method.GetLocation().GetLineSpan().EndLinePosition.Line + 1,
                    ColumnStart = method.GetLocation().GetLineSpan().StartLinePosition.Character + 1,
                    Signature = GetMethodSignature(method),
                    ReturnType = method.ReturnType.ToString()
                });
            }

            return symbols;
        }

        private CodeSymbol? ExtractTypeSymbol(SyntaxNode node, string filePath)
        {
            string name;
            string kind;
            string? visibility = null;
            bool isStatic = false;

            switch (node)
            {
                case ClassDeclarationSyntax classDecl:
                    name = classDecl.Identifier.Text;
                    kind = "class";
                    visibility = GetVisibility(classDecl.Modifiers);
                    isStatic = classDecl.Modifiers.Any(SyntaxKind.StaticKeyword);
                    break;
                case InterfaceDeclarationSyntax ifaceDecl:
                    name = ifaceDecl.Identifier.Text;
                    kind = "interface";
                    visibility = GetVisibility(ifaceDecl.Modifiers);
                    break;
                case StructDeclarationSyntax structDecl:
                    name = structDecl.Identifier.Text;
                    kind = "struct";
                    visibility = GetVisibility(structDecl.Modifiers);
                    break;
                case RecordDeclarationSyntax recordDecl:
                    name = recordDecl.Identifier.Text;
                    kind = "record";
                    visibility = GetVisibility(recordDecl.Modifiers);
                    break;
                case EnumDeclarationSyntax enumDecl:
                    name = enumDecl.Identifier.Text;
                    kind = "enum";
                    visibility = GetVisibility(enumDecl.Modifiers);
                    break;
                case DelegateDeclarationSyntax delegateDecl:
                    name = delegateDecl.Identifier.Text;
                    kind = "delegate";
                    visibility = GetVisibility(delegateDecl.Modifiers);
                    break;
                default:
                    return null;
            }

            var lineSpan = node.GetLocation().GetLineSpan();
            var fullName = GetFullTypeName(node);

            return new CodeSymbol
            {
                Name = name,
                FullName = fullName,
                Kind = kind,
                FilePath = filePath,
                LineStart = lineSpan.StartLinePosition.Line + 1,
                LineEnd = lineSpan.EndLinePosition.Line + 1,
                ColumnStart = lineSpan.StartLinePosition.Character + 1,
                Visibility = visibility,
                IsStatic = isStatic,
                Documentation = GetDocumentation(node)
            };
        }

        private List<CodeSymbol> ExtractMembers(TypeDeclarationSyntax typeDecl, string filePath, string parentName)
        {
            var symbols = new List<CodeSymbol>();

            foreach (var member in typeDecl.Members)
            {
                CodeSymbol? symbol = member switch
                {
                    MethodDeclarationSyntax method => new CodeSymbol
                    {
                        Name = method.Identifier.Text,
                        FullName = $"{parentName}.{method.Identifier.Text}",
                        Kind = "method",
                        Signature = GetMethodSignature(method),
                        ReturnType = method.ReturnType.ToString(),
                        Visibility = GetVisibility(method.Modifiers),
                        IsStatic = method.Modifiers.Any(SyntaxKind.StaticKeyword),
                        Documentation = GetDocumentation(method)
                    },
                    PropertyDeclarationSyntax prop => new CodeSymbol
                    {
                        Name = prop.Identifier.Text,
                        FullName = $"{parentName}.{prop.Identifier.Text}",
                        Kind = "property",
                        ReturnType = prop.Type.ToString(),
                        Visibility = GetVisibility(prop.Modifiers),
                        IsStatic = prop.Modifiers.Any(SyntaxKind.StaticKeyword),
                        Documentation = GetDocumentation(prop)
                    },
                    FieldDeclarationSyntax field => ExtractFieldSymbol(field, parentName),
                    ConstructorDeclarationSyntax ctor => new CodeSymbol
                    {
                        Name = ctor.Identifier.Text,
                        FullName = $"{parentName}.{ctor.Identifier.Text}",
                        Kind = "constructor",
                        Signature = GetConstructorSignature(ctor),
                        Visibility = GetVisibility(ctor.Modifiers),
                        IsStatic = ctor.Modifiers.Any(SyntaxKind.StaticKeyword),
                        Documentation = GetDocumentation(ctor)
                    },
                    EventDeclarationSyntax evt => new CodeSymbol
                    {
                        Name = evt.Identifier.Text,
                        FullName = $"{parentName}.{evt.Identifier.Text}",
                        Kind = "event",
                        ReturnType = evt.Type.ToString(),
                        Visibility = GetVisibility(evt.Modifiers),
                        Documentation = GetDocumentation(evt)
                    },
                    IndexerDeclarationSyntax indexer => new CodeSymbol
                    {
                        Name = "this[]",
                        FullName = $"{parentName}.this[]",
                        Kind = "indexer",
                        ReturnType = indexer.Type.ToString(),
                        Visibility = GetVisibility(indexer.Modifiers),
                        Documentation = GetDocumentation(indexer)
                    },
                    _ => null
                };

                if (symbol != null)
                {
                    var lineSpan = member.GetLocation().GetLineSpan();
                    symbol.FilePath = filePath;
                    symbol.LineStart = lineSpan.StartLinePosition.Line + 1;
                    symbol.LineEnd = lineSpan.EndLinePosition.Line + 1;
                    symbol.ColumnStart = lineSpan.StartLinePosition.Character + 1;
                    symbols.Add(symbol);
                }
            }

            return symbols;
        }

        private CodeSymbol? ExtractFieldSymbol(FieldDeclarationSyntax field, string parentName)
        {
            var variable = field.Declaration.Variables.FirstOrDefault();
            if (variable == null) return null;

            return new CodeSymbol
            {
                Name = variable.Identifier.Text,
                FullName = $"{parentName}.{variable.Identifier.Text}",
                Kind = "field",
                ReturnType = field.Declaration.Type.ToString(),
                Visibility = GetVisibility(field.Modifiers),
                IsStatic = field.Modifiers.Any(SyntaxKind.StaticKeyword),
                Documentation = GetDocumentation(field)
            };
        }

        private List<CodeSymbol> ExtractEnumMembers(EnumDeclarationSyntax enumDecl, string filePath, string parentName)
        {
            var symbols = new List<CodeSymbol>();

            foreach (var member in enumDecl.Members)
            {
                var lineSpan = member.GetLocation().GetLineSpan();
                symbols.Add(new CodeSymbol
                {
                    Name = member.Identifier.Text,
                    FullName = $"{parentName}.{member.Identifier.Text}",
                    Kind = "enum_member",
                    FilePath = filePath,
                    LineStart = lineSpan.StartLinePosition.Line + 1,
                    LineEnd = lineSpan.EndLinePosition.Line + 1,
                    ColumnStart = lineSpan.StartLinePosition.Character + 1,
                    Documentation = GetDocumentation(member)
                });
            }

            return symbols;
        }

        private static string? GetFullTypeName(SyntaxNode node)
        {
            var parts = new List<string>();

            // Get type name
            var typeName = node switch
            {
                TypeDeclarationSyntax td => td.Identifier.Text,
                EnumDeclarationSyntax ed => ed.Identifier.Text,
                DelegateDeclarationSyntax dd => dd.Identifier.Text,
                _ => null
            };

            if (typeName == null) return null;
            parts.Add(typeName);

            // Walk up to find namespace
            var current = node.Parent;
            while (current != null)
            {
                if (current is NamespaceDeclarationSyntax ns)
                {
                    parts.Insert(0, ns.Name.ToString());
                }
                else if (current is FileScopedNamespaceDeclarationSyntax fsns)
                {
                    parts.Insert(0, fsns.Name.ToString());
                }
                else if (current is TypeDeclarationSyntax parentType)
                {
                    parts.Insert(0, parentType.Identifier.Text);
                }
                current = current.Parent;
            }

            return string.Join(".", parts);
        }

        private static string GetVisibility(SyntaxTokenList modifiers)
        {
            if (modifiers.Any(SyntaxKind.PublicKeyword)) return "public";
            if (modifiers.Any(SyntaxKind.PrivateKeyword)) return "private";
            if (modifiers.Any(SyntaxKind.ProtectedKeyword)) return "protected";
            if (modifiers.Any(SyntaxKind.InternalKeyword)) return "internal";
            return "private"; // default for members
        }

        private static string GetMethodSignature(MethodDeclarationSyntax method)
        {
            var sb = new StringBuilder();
            sb.Append(method.ReturnType.ToString());
            sb.Append(' ');
            sb.Append(method.Identifier.Text);
            if (method.TypeParameterList != null)
                sb.Append(method.TypeParameterList.ToString());
            sb.Append(method.ParameterList.ToString());
            return sb.ToString();
        }

        private static string GetMethodSignature(LocalFunctionStatementSyntax method)
        {
            var sb = new StringBuilder();
            sb.Append(method.ReturnType.ToString());
            sb.Append(' ');
            sb.Append(method.Identifier.Text);
            if (method.TypeParameterList != null)
                sb.Append(method.TypeParameterList.ToString());
            sb.Append(method.ParameterList.ToString());
            return sb.ToString();
        }

        private static string GetConstructorSignature(ConstructorDeclarationSyntax ctor)
        {
            return $"{ctor.Identifier.Text}{ctor.ParameterList}";
        }

        private static string? GetDocumentation(SyntaxNode node)
        {
            var trivia = node.GetLeadingTrivia()
                .Select(t => t.GetStructure())
                .OfType<DocumentationCommentTriviaSyntax>()
                .FirstOrDefault();

            if (trivia == null) return null;

            var summary = trivia.DescendantNodes()
                .OfType<XmlElementSyntax>()
                .FirstOrDefault(e => e.StartTag.Name.ToString() == "summary");

            if (summary == null) return null;

            return summary.Content.ToString().Trim();
        }

        private static List<string> GetIndexableFiles(string directory, SqliteConfig config)
        {
            var files = new List<string>();
            var excludeDirs = new HashSet<string>(config.ExcludeDirectories, StringComparer.OrdinalIgnoreCase);
            var extensions = new HashSet<string>(config.IndexExtensions, StringComparer.OrdinalIgnoreCase);

            void ScanDirectory(string dir)
            {
                var dirName = Path.GetFileName(dir);
                if (excludeDirs.Contains(dirName))
                    return;

                try
                {
                    foreach (var file in Directory.GetFiles(dir))
                    {
                        var ext = Path.GetExtension(file);
                        if (extensions.Contains(ext))
                            files.Add(file);
                    }

                    foreach (var subDir in Directory.GetDirectories(dir))
                    {
                        ScanDirectory(subDir);
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    // Skip inaccessible directories
                }
            }

            ScanDirectory(directory);
            return files;
        }

        private static string ComputeFileHash(string filePath)
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            var hashBytes = sha.ComputeHash(stream);
            return Convert.ToBase64String(hashBytes);
        }
    }

    public class IndexResult
    {
        public int TotalFiles { get; set; }
        public int IndexedFiles { get; set; }
        public int SkippedFiles { get; set; }
        public List<string> Errors { get; set; } = new();
    }
}
