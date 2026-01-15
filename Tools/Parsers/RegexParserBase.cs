using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace thuvu.Tools.Parsers
{
    /// <summary>
    /// Base class for regex-based code parsers.
    /// Provides common functionality for extracting symbols using regular expressions.
    /// </summary>
    public abstract class RegexParserBase
    {
        /// <summary>
        /// File extensions this parser handles.
        /// </summary>
        public abstract string[] Extensions { get; }

        /// <summary>
        /// Parse a file and extract symbols.
        /// </summary>
        public abstract Task<List<CodeSymbol>> ParseAsync(string filePath, CancellationToken ct = default);

        /// <summary>
        /// Helper to create a symbol with location info.
        /// </summary>
        protected CodeSymbol CreateSymbol(
            string name,
            string kind,
            string filePath,
            string content,
            int matchIndex,
            int matchLength,
            string? signature = null,
            string? returnType = null,
            string? visibility = null,
            bool isStatic = false,
            string? documentation = null)
        {
            var (lineStart, lineEnd, columnStart) = GetLineInfo(content, matchIndex, matchLength);

            return new CodeSymbol
            {
                Name = name,
                Kind = kind,
                FilePath = filePath,
                LineStart = lineStart,
                LineEnd = lineEnd,
                ColumnStart = columnStart,
                Signature = signature,
                ReturnType = returnType,
                Visibility = visibility,
                IsStatic = isStatic,
                Documentation = documentation
            };
        }

        /// <summary>
        /// Get line number and column from character index.
        /// </summary>
        protected (int lineStart, int lineEnd, int columnStart) GetLineInfo(string content, int index, int length)
        {
            int lineStart = 1;
            int lastNewline = -1;

            for (int i = 0; i < index && i < content.Length; i++)
            {
                if (content[i] == '\n')
                {
                    lineStart++;
                    lastNewline = i;
                }
            }

            int columnStart = index - lastNewline;

            // Count lines in the match
            int lineEnd = lineStart;
            for (int i = index; i < index + length && i < content.Length; i++)
            {
                if (content[i] == '\n')
                    lineEnd++;
            }

            return (lineStart, lineEnd, columnStart);
        }

        /// <summary>
        /// Extract documentation comment before a match.
        /// </summary>
        protected string? ExtractDocumentation(string content, int matchIndex)
        {
            // Look backwards for documentation comments
            int searchStart = matchIndex - 1;
            while (searchStart > 0 && char.IsWhiteSpace(content[searchStart]))
                searchStart--;

            if (searchStart < 3) return null;

            // Check for different comment styles
            var beforeMatch = content.Substring(0, searchStart + 1);

            // JSDoc style: /** ... */
            var jsDocMatch = Regex.Match(beforeMatch, @"/\*\*\s*([\s\S]*?)\*/\s*$");
            if (jsDocMatch.Success)
            {
                return CleanDocComment(jsDocMatch.Groups[1].Value, "* ");
            }

            // Python docstring would be after the def, handled separately

            // Single-line comments: // or #
            var lines = new List<string>();
            var currentPos = searchStart;
            
            while (currentPos > 0)
            {
                // Find start of current line
                int lineStart = beforeMatch.LastIndexOf('\n', currentPos);
                if (lineStart < 0) lineStart = 0;
                else lineStart++;

                var line = beforeMatch.Substring(lineStart, currentPos - lineStart + 1).Trim();

                if (line.StartsWith("//"))
                {
                    lines.Insert(0, line.Substring(2).Trim());
                    currentPos = lineStart - 2;
                }
                else if (line.StartsWith("#") && !line.StartsWith("#!"))
                {
                    lines.Insert(0, line.Substring(1).Trim());
                    currentPos = lineStart - 2;
                }
                else if (string.IsNullOrWhiteSpace(line))
                {
                    currentPos = lineStart - 2;
                }
                else
                {
                    break;
                }
            }

            return lines.Count > 0 ? string.Join(" ", lines) : null;
        }

        private string CleanDocComment(string comment, string linePrefix)
        {
            var lines = comment.Split('\n');
            var cleaned = new List<string>();

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith(linePrefix))
                    trimmed = trimmed.Substring(linePrefix.Length);
                if (!string.IsNullOrWhiteSpace(trimmed))
                    cleaned.Add(trimmed);
            }

            return string.Join(" ", cleaned);
        }
    }
}
