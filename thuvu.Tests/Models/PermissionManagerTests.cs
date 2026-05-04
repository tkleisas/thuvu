using thuvu.Models;

namespace thuvu.Tests.Models;

public class PermissionManagerTests
{
    public PermissionManagerTests()
    {
        PermissionManager.SetCurrentRepoPath("/tmp/thuvu-test-perms");
        PermissionManager.ClearSessionPermissions();
    }

    [Fact]
    public void IsUIAutomationTool_UiCapture_ReturnsTrue()
    {
        Assert.True(PermissionManager.IsUIAutomationTool("ui_capture"));
    }

    [Fact]
    public void IsUIAutomationTool_UiClick_ReturnsTrue()
    {
        Assert.True(PermissionManager.IsUIAutomationTool("ui_click"));
    }

    [Fact]
    public void IsUIAutomationTool_ReadFile_ReturnsFalse()
    {
        Assert.False(PermissionManager.IsUIAutomationTool("read_file"));
    }

    [Fact]
    public void IsUIAutomationTool_Null_ReturnsFalse()
    {
        Assert.False(PermissionManager.IsUIAutomationTool(null!));
    }

    [Fact]
    public void IsAgentCommunicationTool_AgentList_ReturnsTrue()
    {
        Assert.True(PermissionManager.IsAgentCommunicationTool("agent_list"));
    }

    [Fact]
    public void IsAgentCommunicationTool_AgentSubmit_ReturnsTrue()
    {
        Assert.True(PermissionManager.IsAgentCommunicationTool("agent_submit"));
    }

    [Fact]
    public void IsAgentCommunicationTool_ReadFile_ReturnsFalse()
    {
        Assert.False(PermissionManager.IsAgentCommunicationTool("read_file"));
    }

    [Theory]
    [InlineData("search_files", ToolRiskLevel.ReadOnly)]
    [InlineData("read_file", ToolRiskLevel.ReadOnly)]
    [InlineData("git_status", ToolRiskLevel.ReadOnly)]
    [InlineData("rag_search", ToolRiskLevel.ReadOnly)]
    [InlineData("write_file", ToolRiskLevel.Write)]
    [InlineData("apply_patch", ToolRiskLevel.Write)]
    [InlineData("dotnet_build", ToolRiskLevel.Write)]
    [InlineData("run_process", ToolRiskLevel.Write)]
    [InlineData("process_start", ToolRiskLevel.Write)]
    [InlineData("unknown_tool", ToolRiskLevel.Write)] // default to Write for unknown
    public void GetToolRiskLevel_ReturnsCorrectLevel(string tool, ToolRiskLevel expected)
    {
        Assert.Equal(expected, PermissionManager.GetToolRiskLevel(tool));
    }

    [Fact]
    public void TestCheckPermission_ReadOnlyTool_AlwaysAllowed()
    {
        var result = PermissionManager.TestCheckPermission("search_files", "{\"glob\":\"*.cs\"}", 'N');
        Assert.True(result);
    }

    [Fact]
    public void TestCheckPermission_WriteTool_WithChoiceA_Allowed()
    {
        var result = PermissionManager.TestCheckPermission("write_file", "{\"path\":\"test.cs\"}", 'A');
        Assert.True(result);
    }

    [Fact]
    public void TestCheckPermission_WriteTool_WithChoiceN_Denied()
    {
        var result = PermissionManager.TestCheckPermission("write_file", "{\"path\":\"test.cs\"}", 'N');
        Assert.False(result);
    }

    [Fact]
    public void TestCheckPermission_WriteTool_WithChoiceS_Allowed()
    {
        var result = PermissionManager.TestCheckPermission("write_file", "{\"path\":\"test.cs\"}", 'S');
        Assert.True(result);
    }

    [Fact]
    public void TestCheckPermission_WriteTool_WithChoiceO_Allowed()
    {
        var result = PermissionManager.TestCheckPermission("write_file", "{\"path\":\"test.cs\"}", 'O');
        Assert.True(result);
    }

    [Fact]
    public void HandlePermissionChoice_Always_Persists()
    {
        PermissionManager.SetCurrentRepoPath("/tmp/test-unique-" + Guid.NewGuid());
        var result = PermissionManager.HandlePermissionChoice('A', "dotnet_build");
        Assert.True(result);
    }

    [Fact]
    public void HandlePermissionChoice_Session_ReturnsTrue()
    {
        var result = PermissionManager.HandlePermissionChoice('S', "dotnet_build");
        Assert.True(result);
    }

    [Fact]
    public void HandlePermissionChoice_Once_ReturnsTrue()
    {
        var result = PermissionManager.HandlePermissionChoice('O', "dotnet_test");
        Assert.True(result);
    }

    [Fact]
    public void HandlePermissionChoice_No_ReturnsFalse()
    {
        var result = PermissionManager.HandlePermissionChoice('N', "write_file");
        Assert.False(result);
    }

    [Fact]
    public void HandlePermissionChoice_InvalidChoice_ReturnsFalse()
    {
        var result = PermissionManager.HandlePermissionChoice('X', "write_file");
        Assert.False(result);
    }

    [Fact]
    public void ClearSessionPermissions_ClearsDelegates()
    {
        PermissionManager.CustomPermissionPrompt = null;
        PermissionManager.AsyncPermissionPrompt = null;
        PermissionManager.ClearSessionPermissions();
        Assert.Null(PermissionManager.CustomPermissionPrompt);
        Assert.Null(PermissionManager.AsyncPermissionPrompt);
    }

    [Fact]
    public void GetPermissionKeyForTool_ReturnsPathAndTool()
    {
        PermissionManager.SetCurrentRepoPath("/my/repo");
        var key = PermissionManager.GetPermissionKeyForTool("dotnet_build");
        Assert.Contains("/my/repo", key);
        Assert.Contains("dotnet_build", key);
    }

    [Fact]
    public void MCPContext_EnterAndExit_ChangesState()
    {
        PermissionManager.EnterMcpContext();
        Assert.True(PermissionManager.IsInMcpContext);
        PermissionManager.ExitMcpContext();
        Assert.False(PermissionManager.IsInMcpContext);
    }

    [Fact]
    public void EnableUIAutomation_SetsFlag()
    {
        PermissionManager.EnableUIAutomation();
        Assert.True(PermissionManager.UIAutomationEnabled);
    }
}
