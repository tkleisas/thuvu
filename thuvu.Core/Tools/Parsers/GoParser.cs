using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace thuvu.Tools.Parsers
{
    /// <summary>
    /// Regex-based parser for Go files.
    /// Extracts functions, methods, types (struct, interface), and constants.
    /// </summary>
    public class GoParser : RegexParserBase
    {
        public override string[] Extensions => new[] { ".go" };

        // Go patterns
        private static readonly Regex PackagePattern = new(
            @"^package\s+(\w+)",
            RegexOptions.Compiled | RegexOptions.Multiline);

        // Simpler function pattern that handles most Go functions
        private static readonly Regex FunctionPattern = new(
            @"^func\s+(\w+)\s*(?:\[([^\]]+)\])?\s*\(([^)]*)\)\s*(?:(\([^)]+\)|[\w\*\[\]]+(?:\s*[\w\*\[\]]+)*)?\s*)?\s*\{",
            RegexOptions.Compiled | RegexOptions.Multiline);

        private static readonly Regex MethodPattern = new(
            @"^func\s+\((\w+)\s+(\*?)(\w+)\)\s*(\w+)\s*(?:\[([^\]]+)\])?\s*\(([^)]*)\)(?:\s*(?:\(([^)]+)\)|(\w+(?:\s*\*?\s*\w+)?)))?\s*\{",
            RegexOptions.Compiled | RegexOptions.Multiline);

        private static readonly Regex StructPattern = new(
            @"^type\s+(\w+)\s+struct\s*\{",
            RegexOptions.Compiled | RegexOptions.Multiline);

        private static readonly Regex InterfacePattern = new(
            @"^type\s+(\w+)\s+interface\s*\{",
            RegexOptions.Compiled | RegexOptions.Multiline);

        private static readonly Regex TypeAliasPattern = new(
            @"^type\s+(\w+)\s+(?!struct|interface)(\w+(?:\s*\*?\s*\w+)*)",
            RegexOptions.Compiled | RegexOptions.Multiline);

        private static readonly Regex ConstPattern = new(
            @"^const\s+(\w+)(?:\s+(\w+))?\s*=",
            RegexOptions.Compiled | RegexOptions.Multiline);

        private static readonly Regex VarPattern = new(
            @"^var\s+(\w+)\s+(\S+)",
            RegexOptions.Compiled | RegexOptions.Multiline);

        private static readonly Regex ConstBlockPattern = new(
            @"^const\s*\(\s*([\s\S]*?)\n\)",
            RegexOptions.Compiled | RegexOptions.Multiline);

        public override async Task<List<CodeSymbol>> ParseAsync(string filePath, CancellationToken ct = default)
        {
            var symbols = new List<CodeSymbol>();
            var content = await File.ReadAllTextAsync(filePath, ct);

            // Extract package name for context
            string? packageName = null;
            var pkgMatch = PackagePattern.Match(content);
            if (pkgMatch.Success)
                packageName = pkgMatch.Groups[1].Value;

            // Extract structs
            foreach (Match match in StructPattern.Matches(content))
            {
                var doc = ExtractDocumentation(content, match.Index);
                var name = match.Groups[1].Value;
                var isPublic = char.IsUpper(name[0]);

                symbols.Add(CreateSymbol(
                    name: name,
                    kind: "struct",
                    filePath: filePath,
                    content: content,
                    matchIndex: match.Index,
                    matchLength: match.Length,
                    fullName: packageName != null ? $"{packageName}.{name}" : null,
                    documentation: doc,
                    visibility: isPublic ? "public" : "private"
                ));
            }

            // Extract interfaces
            foreach (Match match in InterfacePattern.Matches(content))
            {
                var doc = ExtractDocumentation(content, match.Index);
                var name = match.Groups[1].Value;
                var isPublic = char.IsUpper(name[0]);

                symbols.Add(CreateSymbol(
                    name: name,
                    kind: "interface",
                    filePath: filePath,
                    content: content,
                    matchIndex: match.Index,
                    matchLength: match.Length,
                    fullName: packageName != null ? $"{packageName}.{name}" : null,
                    documentation: doc,
                    visibility: isPublic ? "public" : "private"
                ));
            }

            // Extract type aliases
            foreach (Match match in TypeAliasPattern.Matches(content))
            {
                var doc = ExtractDocumentation(content, match.Index);
                var name = match.Groups[1].Value;
                var baseType = match.Groups[2].Value;
                var isPublic = char.IsUpper(name[0]);

                symbols.Add(CreateSymbol(
                    name: name,
                    kind: "type",
                    filePath: filePath,
                    content: content,
                    matchIndex: match.Index,
                    matchLength: match.Length,
                    returnType: baseType,
                    documentation: doc,
                    visibility: isPublic ? "public" : "private"
                ));
            }

            // Extract functions
            foreach (Match match in FunctionPattern.Matches(content))
            {
                var doc = ExtractDocumentation(content, match.Index);
                var name = match.Groups[1].Value;
                var typeParams = match.Groups[2].Success ? match.Groups[2].Value : null;
                var params_ = match.Groups[3].Value;
                var returnType = match.Groups[4].Success ? match.Groups[4].Value :
                                 match.Groups[5].Success ? match.Groups[5].Value : null;
                var isPublic = char.IsUpper(name[0]);

                var signature = typeParams != null
                    ? $"[{typeParams}]({params_})"
                    : $"({params_})";

                symbols.Add(CreateSymbol(
                    name: name,
                    kind: "function",
                    filePath: filePath,
                    content: content,
                    matchIndex: match.Index,
                    matchLength: match.Length,
                    signature: signature,
                    returnType: returnType?.Trim(),
                    fullName: packageName != null ? $"{packageName}.{name}" : null,
                    documentation: doc,
                    visibility: isPublic ? "public" : "private"
                ));
            }

            // Extract methods (functions with receivers)
            foreach (Match match in MethodPattern.Matches(content))
            {
                var doc = ExtractDocumentation(content, match.Index);
                var receiverName = match.Groups[1].Value;
                var isPointer = match.Groups[2].Value == "*";
                var receiverType = match.Groups[3].Value;
                var name = match.Groups[4].Value;
                var typeParams = match.Groups[5].Success ? match.Groups[5].Value : null;
                var params_ = match.Groups[6].Value;
                var returnType = match.Groups[7].Success ? match.Groups[7].Value :
                                 match.Groups[8].Success ? match.Groups[8].Value : null;
                var isPublic = char.IsUpper(name[0]);

                var receiver = isPointer ? $"*{receiverType}" : receiverType;

                symbols.Add(CreateSymbol(
                    name: name,
                    kind: "method",
                    filePath: filePath,
                    content: content,
                    matchIndex: match.Index,
                    matchLength: match.Length,
                    signature: $"({receiverName} {receiver}) ({params_})",
                    returnType: returnType?.Trim(),
                    fullName: $"{receiverType}.{name}",
                    documentation: doc,
                    visibility: isPublic ? "public" : "private"
                ));
            }

            // Extract single-line constants
            foreach (Match match in ConstPattern.Matches(content))
            {
                var doc = ExtractDocumentation(content, match.Index);
                var name = match.Groups[1].Value;
                var type = match.Groups[2].Success ? match.Groups[2].Value : null;
                var isPublic = char.IsUpper(name[0]);

                symbols.Add(CreateSymbol(
                    name: name,
                    kind: "constant",
                    filePath: filePath,
                    content: content,
                    matchIndex: match.Index,
                    matchLength: match.Length,
                    returnType: type,
                    documentation: doc,
                    visibility: isPublic ? "public" : "private"
                ));
            }

            // Extract package-level variables
            foreach (Match match in VarPattern.Matches(content))
            {
                var doc = ExtractDocumentation(content, match.Index);
                var name = match.Groups[1].Value;
                var type = match.Groups[2].Value;
                var isPublic = char.IsUpper(name[0]);

                symbols.Add(CreateSymbol(
                    name: name,
                    kind: "variable",
                    filePath: filePath,
                    content: content,
                    matchIndex: match.Index,
                    matchLength: match.Length,
                    returnType: type,
                    documentation: doc,
                    visibility: isPublic ? "public" : "private"
                ));
            }

            return symbols;
        }

        /// <summary>
        /// Override CreateSymbol to add fullName parameter support.
        /// </summary>
        protected CodeSymbol CreateSymbol(
            string name,
            string kind,
            string filePath,
            string content,
            int matchIndex,
            int matchLength,
            string? fullName = null,
            string? signature = null,
            string? returnType = null,
            string? documentation = null,
            string? visibility = null,
            bool isStatic = false)
        {
            var symbol = base.CreateSymbol(name, kind, filePath, content, matchIndex, matchLength,
                signature, returnType, visibility, isStatic, documentation);
            symbol.FullName = fullName;
            return symbol;
        }
    }
}
