using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace CodingAgent
{
    public sealed class TestSummary
    {
        public int Passed { get; init; }
        public int Failed { get; init; }
        public int Skipped { get; init; }
        public int Total => Passed + Failed + Skipped;
        public string? Duration { get; init; } // as printed by dotnet
    

        public static TestSummary? ParseDotnetTestStdout(string stdout)
        {
            // Look for lines like:
            // "Passed!  - Failed: 0, Passed: 12, Skipped: 0, Total: 12, Duration: 1 s - ..."
            // or "Failed!  - Failed: 1, Passed: 11, Skipped: 0, Total: 12, Duration: 2 s - ..."
            var lines = stdout.Replace("\r\n", "\n").Split('\n');
            foreach (var line in lines.Reverse())
            {
                var idx = line.IndexOf("Failed:", StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;

                int failed = ExtractInt(line, "Failed:");
                int passed = ExtractInt(line, "Passed:");
                int skipped = ExtractInt(line, "Skipped:");
                string? duration = ExtractAfter(line, "Duration:");

                if (failed >= 0 && passed >= 0 && skipped >= 0)
                    return new TestSummary { Failed = failed, Passed = passed, Skipped = skipped, Duration = duration?.Trim() };
            }
            return null;

            static int ExtractInt(string s, string key)
            {
                var i = s.IndexOf(key, StringComparison.OrdinalIgnoreCase);
                if (i < 0) return -1;
                i += key.Length;
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
                var sb = new StringBuilder();
                while (i < s.Length && char.IsDigit(s[i])) sb.Append(s[i++]);
                return int.TryParse(sb.ToString(), out var n) ? n : -1;
            }

            static string? ExtractAfter(string s, string key)
            {
                var i = s.IndexOf(key, StringComparison.OrdinalIgnoreCase);
                if (i < 0) return null;
                return s[(i + key.Length)..].Trim();
            }
        }
        public static string? TryFindTrxPathFromStdoutOrFS(string stdout, string? searchRoot = null)
        {
            // Prefer explicit path from output: "Results File: <path>"
            var lines = stdout.Replace("\r\n", "\n").Split('\n');
            foreach (var line in lines)
            {
                const string key = "Results File:";
                var idx = line.IndexOf(key, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var path = line[(idx + key.Length)..].Trim();
                    if (File.Exists(path)) return path;
                }
            }

            // Fallback: search for newest *.trx under TestResults
            var root = searchRoot ?? Directory.GetCurrentDirectory();
            var dir = Path.Combine(root, "TestResults");
            if (!Directory.Exists(dir)) return null;

            var trx = Directory.EnumerateFiles(dir, "*.trx", SearchOption.AllDirectories)
                               .OrderByDescending(File.GetLastWriteTimeUtc)
                               .FirstOrDefault();
            return trx;
        }

        public static TestSummary? ParseTrxSummary(string trxPath)
        {
            try
            {
                var x = XDocument.Load(trxPath);
                // TRX: UnitTestResult outcome="Passed|Failed|NotExecuted"
                var outcomes = x.Descendants().Where(e => e.Name.LocalName == "UnitTestResult")
                    .Select(e => (e.Attribute("outcome")?.Value ?? ""))
                    .ToList();

                int passed = outcomes.Count(o => o.Equals("Passed", StringComparison.OrdinalIgnoreCase));
                int failed = outcomes.Count(o => o.Equals("Failed", StringComparison.OrdinalIgnoreCase));
                int skipped = outcomes.Count(o => o.Equals("NotExecuted", StringComparison.OrdinalIgnoreCase));

                return new TestSummary { Passed = passed, Failed = failed, Skipped = skipped, Duration = null };
            }
            catch { return null; }
        }
        public static void PrintTestSummary(TestSummary s)
        {
            var prev = Console.ForegroundColor;
            try
            {
                // Determine overall status
                var isSuccess = s.Failed == 0;
                var icon = isSuccess ? "✓" : "✗";
                var statusColor = isSuccess ? ConsoleColor.Green : ConsoleColor.Red;
                
                // Print styled header
                Console.ForegroundColor = statusColor;
                Console.Write($" {icon} ");
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write("Tests: ");
                
                // Results with visual bars
                if (s.Failed > 0) 
                { 
                    Console.ForegroundColor = ConsoleColor.Red; 
                    Console.Write($"✗ {s.Failed} failed "); 
                }
                if (s.Passed > 0) 
                { 
                    Console.ForegroundColor = ConsoleColor.Green; 
                    Console.Write($"✓ {s.Passed} passed "); 
                }
                if (s.Skipped > 0) 
                { 
                    Console.ForegroundColor = ConsoleColor.Yellow; 
                    Console.Write($"○ {s.Skipped} skipped "); 
                }
                
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"│ Total: {s.Total}");
                if (!string.IsNullOrWhiteSpace(s.Duration)) 
                {
                    Console.Write($" │ ⏱ {s.Duration}");
                }
                Console.WriteLine();
                
                // Print visual progress bar
                if (s.Total > 0)
                {
                    var barWidth = 40;
                    var passedWidth = (int)((s.Passed / (double)s.Total) * barWidth);
                    var failedWidth = (int)((s.Failed / (double)s.Total) * barWidth);
                    var skippedWidth = barWidth - passedWidth - failedWidth;
                    
                    Console.Write("   ");
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.Write(new string('█', passedWidth));
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Write(new string('█', failedWidth));
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.Write(new string('░', skippedWidth));
                    Console.ResetColor();
                    Console.WriteLine();
                }
            }
            finally { Console.ForegroundColor = prev; }
        }

    }
}
