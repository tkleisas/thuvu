using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

internal static class UnifiedDiff
{
    public static (bool applied, string rejects) Apply(string patch, string root)
    {
        var parser = new Parser(patch);
        var files = parser.ParseFiles(out var parseErrors);
        var rejects = new StringBuilder(parseErrors);

        bool allOk = true;

        foreach (var file in files)
        {
            var targetPath = NormalizePath(root, file.NewPath ?? file.OldPath ?? "");
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                rejects.AppendLine("Reject: empty target path in patch.");
                allOk = false;
                continue;
            }

            var exists = File.Exists(targetPath);
            var oldText = exists ? File.ReadAllText(targetPath) : "";
            var oldLines = SplitLines(oldText, out var eol);

            var cursor = 0; // index in oldLines
            var output = new List<string>();

            bool fileOk = true;

            foreach (var hunk in file.Hunks)
            {
                // Copy untouched lines before this hunk's start
                int wantStart = Math.Max(0, hunk.OldStart - 1);
                if (wantStart < cursor)
                {
                    // Overlap means context mismatch—reject
                    rejects.AppendLine($"Reject: overlap before hunk in {file.DisplayPath} at {hunk.Header}");
                    fileOk = false; break;
                }

                // Copy lines up to hunk start
                while (cursor < wantStart && cursor < oldLines.Count)
                {
                    output.Add(oldLines[cursor++]);
                }

                // Apply hunk body
                foreach (var hl in hunk.Lines)
                {
                    if (hl.Prefix == ' ')
                    {
                        // context: must match
                        if (cursor >= oldLines.Count || !LineEq(oldLines[cursor], hl.Text))
                        {
                            rejects.AppendLine($"Reject: context mismatch in {file.DisplayPath} at {hunk.Header}");
                            fileOk = false; break;
                        }
                        output.Add(oldLines[cursor++]);
                    }
                    else if (hl.Prefix == '-')
                    {
                        // delete: must match
                        if (cursor >= oldLines.Count || !LineEq(oldLines[cursor], hl.Text))
                        {
                            rejects.AppendLine($"Reject: delete mismatch in {file.DisplayPath} at {hunk.Header}");
                            fileOk = false; break;
                        }
                        cursor++; // drop it
                    }
                    else if (hl.Prefix == '+')
                    {
                        // add new line
                        output.Add(hl.Text);
                    }
                    else if (hl.Prefix == '\\')
                    {
                        // "\ No newline at end of file" — ignore for now
                    }
                    else
                    {
                        rejects.AppendLine($"Reject: unknown hunk line prefix '{hl.Prefix}' in {file.DisplayPath}");
                        fileOk = false; break;
                    }
                }

                if (!fileOk) break;
            }

            if (fileOk)
            {
                // Copy remaining tail
                while (cursor < oldLines.Count) output.Add(oldLines[cursor++]);

                var resultText = string.Join(eol, output);
                // Preserve trailing newline if present originally or if patch implies it.
                if (oldText.EndsWith(eol) && output.Count > 0 && !resultText.EndsWith(eol))
                    resultText += eol;

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                File.WriteAllText(targetPath, resultText);
            }
            else
            {
                allOk = false;
            }
        }

        return (allOk && rejects.Length == 0, rejects.ToString());
    }

    // ----- helpers -----

    private static string NormalizePath(string root, string p)
    {
        if (string.IsNullOrWhiteSpace(p)) return p;
        p = p.Trim();
        // Remove common prefixes from diffs: a/, b/
        if (p.StartsWith("a/") || p.StartsWith("b/")) p = p[2..];
        if (Path.IsPathRooted(p)) return p;
        return Path.GetFullPath(Path.Combine(string.IsNullOrWhiteSpace(root) ? Directory.GetCurrentDirectory() : root, p));
    }

    private static List<string> SplitLines(string text, out string eol)
    {
        // Detect EOL
        var lf = text.Contains('\n');
        var crlf = text.Contains("\r\n");
        eol = crlf ? "\r\n" : "\n";
        // Split but keep no terminators in items
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1] == "") lines.RemoveAt(lines.Count - 1); // trailing newline -> remove empty artifact
        return lines;
    }

    private static bool LineEq(string a, string b) => string.Equals(a, b, StringComparison.Ordinal);

    // ----- parsing -----

    private sealed class Parser
    {
        private readonly string[] _lines;
        private int _i;

        public Parser(string patch)
        {
            _lines = patch.Replace("\r\n", "\n").Split('\n');
            _i = 0;
        }

        public List<FilePatch> ParseFiles(out string errors)
        {
            var files = new List<FilePatch>();
            var err = new StringBuilder();

            while (_i < _lines.Length)
            {
                // Skip noise until we see a file header pair '--- ' then '+++ '
                string? oldPath = null, newPath = null;
                while (_i < _lines.Length && !StartsWith(_lines[_i], "--- "))
                    _i++;

                if (_i >= _lines.Length) break;
                oldPath = TrimPathAfter(_lines[_i], "--- ");
                _i++;

                if (_i < _lines.Length && StartsWith(_lines[_i], "+++ "))
                {
                    newPath = TrimPathAfter(_lines[_i], "+++ ");
                    _i++;
                }
                else
                {
                    err.AppendLine("Parse: expected +++ after ---");
                    break;
                }

                var fp = new FilePatch { OldPath = oldPath, NewPath = newPath };

                // Read hunks for this file until next file header or EOF
                while (_i < _lines.Length && StartsWith(_lines[_i], "@@ "))
                {
                    var headerLine = _lines[_i++];
                    if (!TryParseHunkHeader(headerLine, out var oldStart, out var oldCount, out var newStart, out var newCount))
                    {
                        err.AppendLine($"Parse: bad hunk header: {headerLine}");
                        break;
                    }

                    var hunk = new Hunk
                    {
                        Header = headerLine,
                        OldStart = oldStart,
                        OldCount = oldCount,
                        NewStart = newStart,
                        NewCount = newCount
                    };

                    // Collect hunk lines until next header or file header or EOF
                    while (_i < _lines.Length)
                    {
                        var l = _lines[_i];
                        if (StartsWith(l, "@@ ") || StartsWith(l, "--- ")) break;

                        if (l.Length == 0) { hunk.Lines.Add(new HunkLine(' ', "")); _i++; continue; }

                        char prefix = l[0];
                        string text = prefix == '\\' ? l : (l.Length > 1 ? l[1..] : "");
                        if (prefix is ' ' or '+' or '-' or '\\')
                        {
                            hunk.Lines.Add(new HunkLine(prefix, text));
                        }
                        else
                        {
                            // Unexpected line; treat as context to be safe
                            hunk.Lines.Add(new HunkLine(' ', l));
                        }
                        _i++;
                    }

                    fp.Hunks.Add(hunk);
                }

                files.Add(fp);
            }

            errors = err.ToString();
            return files;
        }

        private static bool StartsWith(string s, string p) =>
            s.StartsWith(p, StringComparison.Ordinal);

        private static string TrimPathAfter(string line, string prefix)
        {
            var s = line[prefix.Length..].Trim();
            // drop timestamps if present: "path\t<Tab>time" or "path <time>"
            var tab = s.IndexOf('\t');
            if (tab >= 0) s = s[..tab];
            var sp = s.IndexOf(" \t", StringComparison.Ordinal);
            if (sp > 0) s = s[..sp];
            return s;
        }

        private static bool TryParseHunkHeader(string header, out int oStart, out int oCount, out int nStart, out int nCount)
        {
            // @@ -l,s +l,s @@
            oStart = oCount = nStart = nCount = 0;
            try
            {
                int minus = header.IndexOf('-');
                int plus = header.IndexOf('+', minus + 1);
                int at2 = header.IndexOf("@@", plus, StringComparison.Ordinal);

                var left = header.Substring(minus + 1, plus - minus - 2).Trim();  // "l,s"
                var right = header.Substring(plus + 1, at2 - plus - 1).Trim();    // "l,s"

                ParsePair(left, out oStart, out oCount);
                ParsePair(right, out nStart, out nCount);
                return true;
            }
            catch { return false; }

            static void ParsePair(string part, out int start, out int count)
            {
                var pieces = part.Split(',');
                start = int.Parse(pieces[0]);
                count = pieces.Length > 1 ? int.Parse(pieces[1]) : 1;
            }
        }
    }

    private sealed class FilePatch
    {
        public string? OldPath { get; set; }
        public string? NewPath { get; set; }
        public string DisplayPath => NewPath ?? OldPath ?? "<unknown>";
        public List<Hunk> Hunks { get; } = new();
    }

    private sealed class Hunk
    {
        public string Header { get; set; } = "";
        public int OldStart { get; set; }
        public int OldCount { get; set; }
        public int NewStart { get; set; }
        public int NewCount { get; set; }
        public List<HunkLine> Lines { get; } = new();
    }

    private sealed record HunkLine(char Prefix, string Text);
}
