using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace thuvu.Tools.Parsers
{
    /// <summary>
    /// Regex-based parser for Python files.
    /// Extracts classes, functions, methods, and decorated definitions.
    /// </summary>
    public class PythonParser : RegexParserBase
    {
        public override string[] Extensions => new[] { ".py", ".pyw", ".pyi" };

        // Python patterns - note: Python is whitespace-sensitive
        private static readonly Regex ClassPattern = new(
            @"^class\s+(\w+)(?:\s*\(([^)]*)\))?\s*:",
            RegexOptions.Compiled | RegexOptions.Multiline);

        private static readonly Regex FunctionPattern = new(
            @"^(?:async\s+)?def\s+(\w+)\s*\(([^)]*)\)(?:\s*->\s*([^:]+))?\s*:",
            RegexOptions.Compiled | RegexOptions.Multiline);

        private static readonly Regex MethodPattern = new(
            @"^(\s+)(?:async\s+)?def\s+(\w+)\s*\(([^)]*)\)(?:\s*->\s*([^:]+))?\s*:",
            RegexOptions.Compiled | RegexOptions.Multiline);

        private static readonly Regex DecoratorPattern = new(
            @"^@(\w+(?:\.\w+)*)(?:\([^)]*\))?",
            RegexOptions.Compiled | RegexOptions.Multiline);

        private static readonly Regex DocstringPattern = new(
            @"(?:""""""([\s\S]*?)""""""|'''([\s\S]*?)''')",
            RegexOptions.Compiled);

        private static readonly Regex GlobalVarPattern = new(
            @"^([A-Z][A-Z0-9_]*)\s*(?::\s*([^=\n]+))?\s*=",
            RegexOptions.Compiled | RegexOptions.Multiline);

        public override async Task<List<CodeSymbol>> ParseAsync(string filePath, CancellationToken ct = default)
        {
            var symbols = new List<CodeSymbol>();
            var content = await File.ReadAllTextAsync(filePath, ct);

            // Track decorators for the next definition
            var decoratorPositions = new Dictionary<int, List<string>>();
            foreach (Match match in DecoratorPattern.Matches(content))
            {
                // Find the next def/class after this decorator
                int nextDefPos = FindNextDefinition(content, match.Index + match.Length);
                if (nextDefPos >= 0)
                {
                    if (!decoratorPositions.ContainsKey(nextDefPos))
                        decoratorPositions[nextDefPos] = new List<string>();
                    decoratorPositions[nextDefPos].Add(match.Groups[1].Value);
                }
            }

            // Extract classes
            foreach (Match match in ClassPattern.Matches(content))
            {
                var doc = ExtractPythonDocstring(content, match.Index + match.Length);
                var commentDoc = ExtractDocumentation(content, match.Index);
                var bases = match.Groups[2].Success ? match.Groups[2].Value : null;
                
                decoratorPositions.TryGetValue(match.Index, out var decorators);

                symbols.Add(CreateSymbol(
                    name: match.Groups[1].Value,
                    kind: "class",
                    filePath: filePath,
                    content: content,
                    matchIndex: match.Index,
                    matchLength: match.Length,
                    signature: bases != null ? $"({bases})" : null,
                    documentation: doc ?? commentDoc,
                    visibility: match.Groups[1].Value.StartsWith("_") ? "private" : "public",
                    isStatic: decorators?.Contains("staticmethod") == true
                ));
            }

            // Extract top-level functions (no leading whitespace)
            foreach (Match match in FunctionPattern.Matches(content))
            {
                // Check if it's truly at module level (no indentation)
                if (match.Index > 0 && content[match.Index - 1] != '\n' && match.Index != 0)
                    continue;

                var doc = ExtractPythonDocstring(content, match.Index + match.Length);
                var commentDoc = ExtractDocumentation(content, match.Index);
                var returnType = match.Groups[3].Success ? match.Groups[3].Value.Trim() : null;
                var isAsync = match.Value.StartsWith("async");
                
                decoratorPositions.TryGetValue(match.Index, out var decorators);
                var name = match.Groups[1].Value;

                symbols.Add(CreateSymbol(
                    name: name,
                    kind: "function",
                    filePath: filePath,
                    content: content,
                    matchIndex: match.Index,
                    matchLength: match.Length,
                    signature: $"({match.Groups[2].Value})",
                    returnType: returnType,
                    documentation: doc ?? commentDoc,
                    visibility: name.StartsWith("_") ? "private" : "public"
                ));
            }

            // Extract methods (indented def statements)
            foreach (Match match in MethodPattern.Matches(content))
            {
                var indent = match.Groups[1].Value;
                if (string.IsNullOrEmpty(indent)) continue; // Skip top-level

                var doc = ExtractPythonDocstring(content, match.Index + match.Length);
                var returnType = match.Groups[4].Success ? match.Groups[4].Value.Trim() : null;
                var name = match.Groups[2].Value;
                var params_ = match.Groups[3].Value;
                
                // Determine if static/class method based on first param or decorator
                var isStatic = !params_.Contains("self") && !params_.Contains("cls");
                var visibility = name.StartsWith("__") && name.EndsWith("__") ? "public" :
                                 name.StartsWith("_") ? "private" : "public";

                symbols.Add(CreateSymbol(
                    name: name,
                    kind: "method",
                    filePath: filePath,
                    content: content,
                    matchIndex: match.Index,
                    matchLength: match.Length,
                    signature: $"({params_})",
                    returnType: returnType,
                    documentation: doc,
                    visibility: visibility,
                    isStatic: isStatic
                ));
            }

            // Extract module-level constants (UPPER_CASE names)
            foreach (Match match in GlobalVarPattern.Matches(content))
            {
                // Check if truly at module level
                if (match.Index > 0 && content[match.Index - 1] != '\n' && match.Index != 0)
                    continue;

                var doc = ExtractDocumentation(content, match.Index);
                var type = match.Groups[2].Success ? match.Groups[2].Value.Trim() : null;

                symbols.Add(CreateSymbol(
                    name: match.Groups[1].Value,
                    kind: "variable",
                    filePath: filePath,
                    content: content,
                    matchIndex: match.Index,
                    matchLength: match.Length,
                    returnType: type,
                    documentation: doc,
                    visibility: "public"
                ));
            }

            return symbols;
        }

        private int FindNextDefinition(string content, int startPos)
        {
            // Find next 'def ' or 'class ' after startPos
            int defPos = content.IndexOf("def ", startPos);
            int classPos = content.IndexOf("class ", startPos);

            if (defPos < 0) return classPos;
            if (classPos < 0) return defPos;
            return defPos < classPos ? defPos : classPos;
        }

        private string? ExtractPythonDocstring(string content, int afterDefPos)
        {
            // Skip to next line
            int pos = content.IndexOf('\n', afterDefPos);
            if (pos < 0) return null;
            pos++;

            // Skip whitespace
            while (pos < content.Length && (content[pos] == ' ' || content[pos] == '\t'))
                pos++;

            // Check for docstring
            if (pos + 3 >= content.Length) return null;

            string quote;
            if (content.Substring(pos, 3) == "\"\"\"")
                quote = "\"\"\"";
            else if (content.Substring(pos, 3) == "'''")
                quote = "'''";
            else
                return null;

            int start = pos + 3;
            int end = content.IndexOf(quote, start);
            if (end < 0) return null;

            var docstring = content.Substring(start, end - start).Trim();
            
            // Clean up docstring - take first paragraph
            var firstPara = docstring.Split(new[] { "\n\n" }, System.StringSplitOptions.None)[0];
            return firstPara.Replace("\n", " ").Trim();
        }
    }
}
