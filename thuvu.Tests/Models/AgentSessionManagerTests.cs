using thuvu.Models;
using System.Diagnostics;

namespace thuvu.Tests.Models;

public class AgentSessionManagerTests
{
    private string CreateGitRepo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "thuvu-git-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var prevWorkDir = AgentConfig.Config.WorkDirectory;
        try
        {
            AgentConfig.Config.WorkDirectory = dir;
            RunGit(dir, "init");
            RunGit(dir, "config user.email test@thuvu.local");
            RunGit(dir, "config user.name \"THUVU Test\"");
            File.WriteAllText(Path.Combine(dir, "README.md"), "# Test Repo\n");
            RunGit(dir, "add README.md");
            RunGit(dir, "commit -m \"initial commit\"");
        }
        finally
        {
            AgentConfig.Config.WorkDirectory = prevWorkDir;
        }
        return dir;
    }

    private static string RunGit(string workDir, string args)
    {
        var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = args,
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        proc.Start();
        proc.WaitForExit(5000);
        return proc.StandardOutput.ReadToEnd();
    }

    [Fact]
    public void GenerateAgentId_ReturnsUniqueIds()
    {
        var id1 = AgentSessionManager.GenerateAgentId();
        var id2 = AgentSessionManager.GenerateAgentId();
        Assert.NotEqual(id1, id2);
        Assert.StartsWith("thuvu-", id1);
    }

    [Fact]
    public async Task StartSessionAsync_CreatesBranch()
    {
        var dir = CreateGitRepo();
        var prevWorkDir = AgentConfig.Config.WorkDirectory;
        try
        {
            AgentConfig.Config.WorkDirectory = dir;
            var session = await AgentSessionManager.StartSessionAsync("test-feature");
            Assert.NotNull(session);
            Assert.Equal("in_progress", session.Status);
            Assert.Contains("agent/thuvu-", session.BranchName);
            await AgentSessionManager.AbortSessionAsync(deleteBranch: true);
        }
        finally
        {
            AgentConfig.Config.WorkDirectory = prevWorkDir;
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task CommitAsync_CommitsChanges()
    {
        var dir = CreateGitRepo();
        var prevWorkDir = AgentConfig.Config.WorkDirectory;
        try
        {
            AgentConfig.Config.WorkDirectory = dir;
            var session = await AgentSessionManager.StartSessionAsync("add-file");
            File.WriteAllText(Path.Combine(dir, "newfile.txt"), "new content");
            var committed = await AgentSessionManager.CommitAsync("feat", "add new file");
            Assert.True(committed);
            Assert.Equal(1, session.CommitCount);
            await AgentSessionManager.AbortSessionAsync(deleteBranch: true);
        }
        finally
        {
            AgentConfig.Config.WorkDirectory = prevWorkDir;
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task CreateCheckpointAsync_CreatesTag()
    {
        var dir = CreateGitRepo();
        var prevWorkDir = AgentConfig.Config.WorkDirectory;
        try
        {
            AgentConfig.Config.WorkDirectory = dir;
            var session = await AgentSessionManager.StartSessionAsync("checkpoint-test");
            File.WriteAllText(Path.Combine(dir, "check.txt"), "data");
            await AgentSessionManager.CommitAsync("test", "checkpoint commit");
            var checkpoint = await AgentSessionManager.CreateCheckpointAsync("milestone-1");
            Assert.NotNull(checkpoint);
            Assert.Contains("checkpoint-1", checkpoint.Tag);
            Assert.Equal(1, session.Checkpoints.Count);
            await AgentSessionManager.AbortSessionAsync(deleteBranch: true);
        }
        finally
        {
            AgentConfig.Config.WorkDirectory = prevWorkDir;
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task RollbackAsync_RestoresToCheckpoint()
    {
        var dir = CreateGitRepo();
        var prevWorkDir = AgentConfig.Config.WorkDirectory;
        try
        {
            AgentConfig.Config.WorkDirectory = dir;
            var session = await AgentSessionManager.StartSessionAsync("rollback-test");
            await AgentSessionManager.CommitAsync("feat", "first change");
            var checkpoint = await AgentSessionManager.CreateCheckpointAsync("safe-point");
            Assert.NotNull(checkpoint);
            File.WriteAllText(Path.Combine(dir, "bad.txt"), "bad data");
            await AgentSessionManager.CommitAsync("feat", "bad change");
            var rolled = await AgentSessionManager.RollbackAsync(checkpoint.Tag);
            Assert.True(rolled);
            Assert.False(File.Exists(Path.Combine(dir, "bad.txt")));
            await AgentSessionManager.AbortSessionAsync(deleteBranch: true);
        }
        finally
        {
            AgentConfig.Config.WorkDirectory = prevWorkDir;
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task CompleteSessionAsync_MergesToBase()
    {
        var dir = CreateGitRepo();
        var prevWorkDir = AgentConfig.Config.WorkDirectory;
        try
        {
            AgentConfig.Config.WorkDirectory = dir;
            var session = await AgentSessionManager.StartSessionAsync("merge-test");
            File.WriteAllText(Path.Combine(dir, "merge-file.txt"), "merged content");
            await AgentSessionManager.CommitAsync("feat", "merge commit");
            var completed = await AgentSessionManager.CompleteSessionAsync(merge: true, deleteBranch: true);
            Assert.True(completed);
            Assert.Equal("completed", session.Status);
            Assert.True(File.Exists(Path.Combine(dir, "merge-file.txt")));
        }
        finally
        {
            AgentConfig.Config.WorkDirectory = prevWorkDir;
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void SanitizeBranchName_RemovesInvalidCharacters()
    {
        var dir = CreateGitRepo();
        var prevWorkDir = AgentConfig.Config.WorkDirectory;
        try
        {
            AgentConfig.Config.WorkDirectory = dir;
            var session = AgentSessionManager.StartSessionAsync("Fix/Login: Bug").GetAwaiter().GetResult();
            var branchTaskPart = session.BranchName.Substring(session.BranchName.IndexOf("agent/") + "agent/".Length);
            var taskDescription = branchTaskPart.Substring(branchTaskPart.IndexOf('/') + 1);
            Assert.DoesNotContain("/", taskDescription);
            Assert.DoesNotContain(":", taskDescription);
            AgentSessionManager.AbortSessionAsync(deleteBranch: true).GetAwaiter().GetResult();
        }
        finally
        {
            AgentConfig.Config.WorkDirectory = prevWorkDir;
            Directory.Delete(dir, true);
        }
    }
}
