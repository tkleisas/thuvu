using System.Text.Json;
using System.Text.Json.Serialization;

namespace thuvu.Desktop.Models;

/// <summary>
/// Project configuration stored in a .thuvu file
/// </summary>
public class ProjectConfig
{
    /// <summary>Project display name</summary>
    public string Name { get; set; } = "Untitled Project";

    /// <summary>Working directory for file operations (relative to .thuvu file or absolute)</summary>
    public string WorkDirectory { get; set; } = ".";

    /// <summary>Default model ID override (empty = use global settings)</summary>
    public string DefaultModelId { get; set; } = "";

    /// <summary>Custom system prompt for this project</summary>
    public string SystemPrompt { get; set; } = "";

    /// <summary>Files/directories to exclude from the file tree</summary>
    public List<string> ExcludePatterns { get; set; } = new() { "bin", "obj", "node_modules", ".git" };

    /// <summary>Full path to the .thuvu file</summary>
    [JsonIgnore]
    public string FilePath { get; set; } = "";

    /// <summary>Directory containing the .thuvu file</summary>
    [JsonIgnore]
    public string ProjectDirectory => Path.GetDirectoryName(FilePath) ?? "";

    /// <summary>Resolved absolute work directory</summary>
    [JsonIgnore]
    public string ResolvedWorkDirectory
    {
        get
        {
            if (Path.IsPathRooted(WorkDirectory))
                return Path.GetFullPath(WorkDirectory);
            return Path.GetFullPath(Path.Combine(ProjectDirectory, WorkDirectory));
        }
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
    };

    public void Save()
    {
        if (string.IsNullOrEmpty(FilePath)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var json = JsonSerializer.Serialize(this, _jsonOptions);
        File.WriteAllText(FilePath, json);
    }

    public static ProjectConfig Load(string path)
    {
        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<ProjectConfig>(json, _jsonOptions) ?? new ProjectConfig();
        config.FilePath = Path.GetFullPath(path);
        return config;
    }

    public static ProjectConfig CreateNew(string directory, string? name = null)
    {
        var config = new ProjectConfig
        {
            Name = name ?? Path.GetFileName(directory) ?? "Project",
            WorkDirectory = ".",
            FilePath = Path.Combine(directory, ".thuvu")
        };
        config.Save();
        return config;
    }
}
