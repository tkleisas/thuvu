using System;
using System.IO;
using thuvu.Models;

namespace thuvu
{
    public class PermissionSystemDemo
    {
        public static void RunDemo()
        {
            Console.WriteLine("=== Permission System Demo ===\n");
            
            // Initialize
            AgentConfig.LoadConfig();
            PermissionManager.SetCurrentRepoPath(Directory.GetCurrentDirectory());
            
            // Test 1: Read-only tool (should be allowed automatically)
            Console.WriteLine("Test 1: Read-only tool (search_files)");
            var isAllowed1 = PermissionManager.TestCheckPermission("search_files", "{\"glob\": \"*.cs\"}", 'N');
            Console.WriteLine($"Result: {(isAllowed1 ? "ALLOWED" : "DENIED")} (Expected: ALLOWED)\n");
            
            // Test 2: Write tool with denial
            Console.WriteLine("Test 2: Write tool (write_file) with user denial");
            var isAllowed2 = PermissionManager.TestCheckPermission("write_file", "{\"path\": \"test.txt\"}", 'N');
            Console.WriteLine($"Result: {(isAllowed2 ? "ALLOWED" : "DENIED")} (Expected: DENIED)\n");
            
            // Test 3: Write tool with once permission
            Console.WriteLine("Test 3: Write tool (write_file) with once permission");
            var isAllowed3 = PermissionManager.TestCheckPermission("write_file", "{\"path\": \"test.txt\"}", 'O');
            Console.WriteLine($"Result: {(isAllowed3 ? "ALLOWED" : "DENIED")} (Expected: ALLOWED)\n");
            
            // Test 4: Write tool with session permission
            Console.WriteLine("Test 4: Write tool (apply_patch) with session permission");
            var isAllowed4 = PermissionManager.TestCheckPermission("apply_patch", "{\"patch\": \"test\"}", 'S');
            Console.WriteLine($"Result: {(isAllowed4 ? "ALLOWED" : "DENIED")} (Expected: ALLOWED)");
            
            // Test 5: Same tool again (should use session permission)
            Console.WriteLine("Test 5: Same tool again (should use cached session permission)");
            var isAllowed5 = PermissionManager.TestCheckPermission("apply_patch", "{\"patch\": \"test2\"}", 'N');
            Console.WriteLine($"Result: {(isAllowed5 ? "ALLOWED" : "DENIED")} (Expected: ALLOWED)\n");
            
            // Test 6: Write tool with always permission
            Console.WriteLine("Test 6: Write tool (run_process) with always permission");
            var isAllowed6 = PermissionManager.TestCheckPermission("run_process", "{\"cmd\": \"git\"}", 'A');
            Console.WriteLine($"Result: {(isAllowed6 ? "ALLOWED" : "DENIED")} (Expected: ALLOWED)");
            
            // Test 7: Same tool again (should use persistent permission)
            Console.WriteLine("Test 7: Same tool again (should use cached persistent permission)");
            var isAllowed7 = PermissionManager.TestCheckPermission("run_process", "{\"cmd\": \"ls\"}", 'N');
            Console.WriteLine($"Result: {(isAllowed7 ? "ALLOWED" : "DENIED")} (Expected: ALLOWED)\n");
            
            Console.WriteLine("=== Demo Complete ===");
            Console.WriteLine($"Config file location: {AgentConfig.GetConfigPath()}");
        }
    }
}