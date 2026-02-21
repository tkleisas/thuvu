using System.Text.Json;
using System.Text.Json.Serialization;

namespace thuvu.Models;

public class LspConfig
{
    public static LspConfig Config { get; private set; } = new();

    public bool Enabled { get; set; } = true;
    public bool AutoDiagnostics { get; set; } = true;
    public int DiagnosticsTimeoutMs { get; set; } = 3000;
    public Dictionary<string, LspServerConfig> Servers { get; set; } = new()
    {
        ["omnisharp"] = new LspServerConfig
        {
            Path = "",
            AutoDownload = true,
            Extensions = new[] { ".cs", ".csx" }
        }
    };

    public static void LoadConfig()
    {
        var path = AgentConfig.GetConfigPath();
        if (!File.Exists(path)) return;

        try
        {
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            if (root.TryGetProperty("LspConfig", out var section))
            {
                Config = JsonSerializer.Deserialize<LspConfig>(section.GetRawText(), options) ?? new LspConfig();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: Failed to load LspConfig: {ex.Message}");
        }
    }

    public LspServerConfig? GetServerConfig(string serverId)
    {
        return Servers.TryGetValue(serverId, out var cfg) ? cfg : null;
    }
}

public class LspServerConfig
{
    public string Path { get; set; } = "";
    public bool AutoDownload { get; set; } = true;
    public bool Disabled { get; set; } = false;
    public string[] Extensions { get; set; } = Array.Empty<string>();
    public Dictionary<string, string>? Environment { get; set; }
}
