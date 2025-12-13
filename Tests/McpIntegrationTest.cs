using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using thuvu.Models;

namespace thuvu.Tests
{
    /// <summary>
    /// Integration tests for MCP functionality (can run without Deno for bridge tests)
    /// </summary>
    public static class McpIntegrationTest
    {
        public static async Task RunAllTests()
        {
            Console.WriteLine("═══════════════════════════════════════════════════════════");
            Console.WriteLine("  MCP Integration Tests");
            Console.WriteLine("═══════════════════════════════════════════════════════════");
            Console.WriteLine();

            var passed = 0;
            var failed = 0;

            // Test 1: McpConfig
            if (await TestMcpConfig())
                passed++;
            else
                failed++;

            // Test 2: McpBridge tool registration
            if (await TestMcpBridgeTools())
                passed++;
            else
                failed++;

            // Test 3: McpBridge catalog tools
            if (await TestCatalogTools())
                passed++;
            else
                failed++;

            // Test 4: SkillManager
            if (await TestSkillManager())
                passed++;
            else
                failed++;

            // Test 5: Path validation
            if (await TestPathValidation())
                passed++;
            else
                failed++;

            // Test 6: Deno availability check
            if (await TestDenoCheck())
                passed++;
            else
                failed++;

            Console.WriteLine();
            Console.WriteLine("═══════════════════════════════════════════════════════════");
            Console.ForegroundColor = passed > 0 && failed == 0 ? ConsoleColor.Green : ConsoleColor.Yellow;
            Console.WriteLine($"  Results: {passed} passed, {failed} failed");
            Console.ResetColor();
            Console.WriteLine("═══════════════════════════════════════════════════════════");
        }

        private static async Task<bool> TestMcpConfig()
        {
            Console.Write("  [1] McpConfig load/save... ");
            try
            {
                // Test config properties
                var config = McpConfig.Instance;
                var originalLevel = config.PermissionLevel;
                
                config.PermissionLevel = "test-level";
                if (config.PermissionLevel != "test-level")
                    throw new Exception("Config property set failed");

                config.PermissionLevel = originalLevel;

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("✓ PASSED");
                Console.ResetColor();
                return await Task.FromResult(true);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"✗ FAILED: {ex.Message}");
                Console.ResetColor();
                return false;
            }
        }

        private static async Task<bool> TestMcpBridgeTools()
        {
            Console.Write("  [2] McpBridge tool registration... ");
            try
            {
                var bridge = new McpBridge();
                var tools = bridge.GetAvailableTools();

                // Check for expected tools
                var expectedTools = new[] { "read_file", "write_file", "search_files", "dotnet_build", "git_status" };
                foreach (var tool in expectedTools)
                {
                    if (!System.Linq.Enumerable.Contains(tools, tool))
                        throw new Exception($"Missing tool: {tool}");
                }

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("✓ PASSED");
                Console.ResetColor();
                return await Task.FromResult(true);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"✗ FAILED: {ex.Message}");
                Console.ResetColor();
                return false;
            }
        }

        private static async Task<bool> TestCatalogTools()
        {
            Console.Write("  [3] Catalog tools... ");
            try
            {
                var bridge = new McpBridge();
                
                // Test catalog_list
                var request = new JsonRpcRequest
                {
                    Id = 1,
                    Method = "catalog_list",
                    Params = JsonDocument.Parse("{}").RootElement
                };

                var response = await bridge.HandleRequestAsync(request, CancellationToken.None);
                if (response.Error != null)
                    throw new Exception($"catalog_list failed: {response.Error.Message}");

                // Test catalog_search
                request = new JsonRpcRequest
                {
                    Id = 2,
                    Method = "catalog_search",
                    Params = JsonDocument.Parse("{\"query\": \"file\"}").RootElement
                };

                response = await bridge.HandleRequestAsync(request, CancellationToken.None);
                if (response.Error != null)
                    throw new Exception($"catalog_search failed: {response.Error.Message}");

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("✓ PASSED");
                Console.ResetColor();
                return true;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"✗ FAILED: {ex.Message}");
                Console.ResetColor();
                return false;
            }
        }

        private static async Task<bool> TestSkillManager()
        {
            Console.Write("  [4] SkillManager... ");
            try
            {
                // Ensure directory exists
                SkillManager.EnsureDirectoryExists();

                // Test listing (should not throw)
                var skills = SkillManager.ListSkills();

                // Test save and delete
                var testName = $"test-skill-{Guid.NewGuid():N}";
                SkillManager.SaveSkill(testName, "// test code", "Test skill");

                var skill = SkillManager.GetSkill(testName);
                if (skill == null)
                    throw new Exception("Skill not found after save");

                var code = SkillManager.LoadSkillCode(testName);
                if (string.IsNullOrEmpty(code))
                    throw new Exception("Skill code not loaded");

                SkillManager.DeleteSkill(testName);

                skill = SkillManager.GetSkill(testName);
                if (skill != null)
                    throw new Exception("Skill still exists after delete");

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("✓ PASSED");
                Console.ResetColor();
                return await Task.FromResult(true);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"✗ FAILED: {ex.Message}");
                Console.ResetColor();
                return false;
            }
        }

        private static async Task<bool> TestPathValidation()
        {
            Console.Write("  [5] Path validation... ");
            try
            {
                var bridge = new McpBridge();

                // Test with safe path (current directory)
                var request = new JsonRpcRequest
                {
                    Id = 1,
                    Method = "read_file",
                    Params = JsonDocument.Parse("{\"path\": \"README.md\"}").RootElement
                };

                // This should not fail due to path validation
                // (might fail for other reasons like file not found, but that's OK)
                var response = await bridge.HandleRequestAsync(request, CancellationToken.None);
                
                // Check that path validation doesn't reject valid project paths
                // The error should NOT be "Path validation failed"
                if (response.Error != null && response.Error.Message.Contains("Path validation failed"))
                {
                    throw new Exception("Valid path was rejected");
                }

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("✓ PASSED");
                Console.ResetColor();
                return true;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"✗ FAILED: {ex.Message}");
                Console.ResetColor();
                return false;
            }
        }

        private static async Task<bool> TestDenoCheck()
        {
            Console.Write("  [6] Deno availability check... ");
            try
            {
                var available = await McpCodeExecutor.IsDenoAvailableAsync();
                
                if (available)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("✓ PASSED (Deno is installed)");
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("⚠ PASSED (Deno not installed - install for full MCP support)");
                }
                Console.ResetColor();
                return true;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"✗ FAILED: {ex.Message}");
                Console.ResetColor();
                return false;
            }
        }
    }
}
