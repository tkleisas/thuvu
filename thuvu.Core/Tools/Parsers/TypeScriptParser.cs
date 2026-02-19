using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace thuvu.Tools.Parsers
{
    /// <summary>
    /// Regex-based parser for TypeScript and JavaScript files.
    /// Extracts classes, interfaces, functions, methods, and exports.
    /// </summary>
    public class TypeScriptParser : RegexParserBase
    {
        public override string[] Extensions => new[] { ".ts", ".tsx", ".js", ".jsx", ".mjs", ".mts" };

        // Patterns for TypeScript/JavaScript constructs
        private static readonly Regex ClassPattern = new(
            @"(?:export\s+)?(?:abstract\s+)?class\s+(\w+)(?:\s+extends\s+(\w+))?(?:\s+implements\s+([\w,\s]+))?\s*\{",
            RegexOptions.Compiled);

        private static readonly Regex InterfacePattern = new(
            @"(?:export\s+)?interface\s+(\w+)(?:\s+extends\s+([\w,\s]+))?\s*\{",
            RegexOptions.Compiled);

        private static readonly Regex TypeAliasPattern = new(
            @"(?:export\s+)?type\s+(\w+)(?:<[^>]+>)?\s*=",
            RegexOptions.Compiled);

        private static readonly Regex EnumPattern = new(
            @"(?:export\s+)?(?:const\s+)?enum\s+(\w+)\s*\{",
            RegexOptions.Compiled);

        private static readonly Regex FunctionPattern = new(
            @"(?:export\s+)?(?:async\s+)?function\s+(\w+)\s*(?:<[^>]+>)?\s*\(([^)]*)\)(?:\s*:\s*([^\{]+))?\s*\{",
            RegexOptions.Compiled);

        private static readonly Regex ArrowFunctionPattern = new(
            @"(?:export\s+)?(?:const|let|var)\s+(\w+)\s*(?::\s*[^=]+)?\s*=\s*(?:async\s+)?(?:\([^)]*\)|[^=])\s*=>\s*",
            RegexOptions.Compiled);

        private static readonly Regex MethodPattern = new(
            @"(?:(?:public|private|protected|static|async|readonly)\s+)*(\w+)\s*(?:<[^>]+>)?\s*\(([^)]*)\)(?:\s*:\s*([^\{;]+))?\s*[\{;]",
            RegexOptions.Compiled);

        private static readonly Regex PropertyPattern = new(
            @"(?:(?:public|private|protected|static|readonly)\s+)+(\w+)(?:\?)?(?:\s*:\s*([^;=]+))?(?:\s*[;=])",
            RegexOptions.Compiled);

        private static readonly Regex ConstExportPattern = new(
            @"export\s+(?:const|let|var)\s+(\w+)(?:\s*:\s*([^=]+))?\s*=",
            RegexOptions.Compiled);

        public override async Task<List<CodeSymbol>> ParseAsync(string filePath, CancellationToken ct = default)
        {
            var symbols = new List<CodeSymbol>();
            var content = await File.ReadAllTextAsync(filePath, ct);

            // Extract classes
            foreach (Match match in ClassPattern.Matches(content))
            {
                var doc = ExtractDocumentation(content, match.Index);
                var isExport = match.Value.StartsWith("export");
                var isAbstract = match.Value.Contains("abstract");
                
                symbols.Add(CreateSymbol(
                    name: match.Groups[1].Value,
                    kind: "class",
                    filePath: filePath,
                    content: content,
                    matchIndex: match.Index,
                    matchLength: match.Length,
                    signature: match.Groups[2].Success ? $"extends {match.Groups[2].Value}" : null,
                    visibility: isExport ? "public" : "private",
                    isStatic: isAbstract,
                    documentation: doc
                ));
            }

            // Extract interfaces
            foreach (Match match in InterfacePattern.Matches(content))
            {
                var doc = ExtractDocumentation(content, match.Index);
                var isExport = match.Value.StartsWith("export");

                symbols.Add(CreateSymbol(
                    name: match.Groups[1].Value,
                    kind: "interface",
                    filePath: filePath,
                    content: content,
                    matchIndex: match.Index,
                    matchLength: match.Length,
                    visibility: isExport ? "public" : "private",
                    documentation: doc
                ));
            }

            // Extract type aliases
            foreach (Match match in TypeAliasPattern.Matches(content))
            {
                var doc = ExtractDocumentation(content, match.Index);
                var isExport = match.Value.StartsWith("export");

                symbols.Add(CreateSymbol(
                    name: match.Groups[1].Value,
                    kind: "type",
                    filePath: filePath,
                    content: content,
                    matchIndex: match.Index,
                    matchLength: match.Length,
                    visibility: isExport ? "public" : "private",
                    documentation: doc
                ));
            }

            // Extract enums
            foreach (Match match in EnumPattern.Matches(content))
            {
                var doc = ExtractDocumentation(content, match.Index);
                var isExport = match.Value.StartsWith("export");

                symbols.Add(CreateSymbol(
                    name: match.Groups[1].Value,
                    kind: "enum",
                    filePath: filePath,
                    content: content,
                    matchIndex: match.Index,
                    matchLength: match.Length,
                    visibility: isExport ? "public" : "private",
                    documentation: doc
                ));
            }

            // Extract functions
            foreach (Match match in FunctionPattern.Matches(content))
            {
                var doc = ExtractDocumentation(content, match.Index);
                var isExport = match.Value.StartsWith("export");
                var isAsync = match.Value.Contains("async");
                var returnType = match.Groups[3].Success ? match.Groups[3].Value.Trim() : null;

                symbols.Add(CreateSymbol(
                    name: match.Groups[1].Value,
                    kind: "function",
                    filePath: filePath,
                    content: content,
                    matchIndex: match.Index,
                    matchLength: match.Length,
                    signature: $"({match.Groups[2].Value})",
                    returnType: returnType,
                    visibility: isExport ? "public" : "private",
                    documentation: doc
                ));
            }

            // Extract arrow function exports
            foreach (Match match in ArrowFunctionPattern.Matches(content))
            {
                var doc = ExtractDocumentation(content, match.Index);
                var isExport = match.Value.StartsWith("export");

                symbols.Add(CreateSymbol(
                    name: match.Groups[1].Value,
                    kind: "function",
                    filePath: filePath,
                    content: content,
                    matchIndex: match.Index,
                    matchLength: match.Length,
                    visibility: isExport ? "public" : "private",
                    documentation: doc
                ));
            }

            // Extract const exports (often important API surfaces)
            foreach (Match match in ConstExportPattern.Matches(content))
            {
                // Skip if already matched as arrow function
                var name = match.Groups[1].Value;
                if (symbols.Exists(s => s.Name == name)) continue;

                var doc = ExtractDocumentation(content, match.Index);
                var type = match.Groups[2].Success ? match.Groups[2].Value.Trim() : null;

                symbols.Add(CreateSymbol(
                    name: name,
                    kind: "variable",
                    filePath: filePath,
                    content: content,
                    matchIndex: match.Index,
                    matchLength: match.Length,
                    returnType: type,
                    visibility: "public",
                    documentation: doc
                ));
            }

            return symbols;
        }
    }
}
