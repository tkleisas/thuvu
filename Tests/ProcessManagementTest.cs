using System;
using System.Text.Json;
using System.Threading.Tasks;
using thuvu.Tools.ProcessManagement;

namespace thuvu.Tests
{
    /// <summary>
    /// Tests for the process management tools
    /// </summary>
    public static class ProcessManagementTest
    {
        public static async Task RunAllTestsAsync()
        {
            Console.WriteLine();
            Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║         PROCESS MANAGEMENT TOOLS - TEST SUITE                ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
            Console.WriteLine();

            await Test1_ProcessStartAsync();
            await Test2_ProcessReadAsync();
            await Test3_ProcessStatusAsync();
            await Test4_ProcessWriteAsync();
            await Test5_ProcessStopAsync();
            await Test6_ProcessWithOutputAsync();
            
            // Cleanup any remaining sessions
            ProcessSessionManager.Instance.StopAllSessions();
            
            Console.WriteLine();
            Console.WriteLine("All tests completed!");
        }

        private static async Task Test1_ProcessStartAsync()
        {
            Console.WriteLine("─── Test 1: process_start ───");
            
            try
            {
                // Start a simple process that echoes and exits
                var result = await ProcessToolImpl.ProcessStartAsync(JsonSerializer.Serialize(new
                {
                    cmd = "dotnet",
                    args = new[] { "--version" }
                }));

                var json = JsonDocument.Parse(result);
                var success = json.RootElement.GetProperty("success").GetBoolean();
                
                if (success)
                {
                    var sessionId = json.RootElement.GetProperty("session_id").GetString();
                    var pid = json.RootElement.GetProperty("pid").GetInt32();
                    Console.WriteLine($"  ✓ SUCCESS: Started process with session_id={sessionId}, pid={pid}");
                    
                    // Wait a bit for the process to complete
                    await Task.Delay(500);
                    
                    // Clean up
                    ProcessSessionManager.Instance.StopSession(sessionId!);
                }
                else
                {
                    var error = json.RootElement.GetProperty("error").GetString();
                    Console.WriteLine($"  ✗ FAILED: {error}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✗ EXCEPTION: {ex.Message}");
            }
            Console.WriteLine();
        }

        private static async Task Test2_ProcessReadAsync()
        {
            Console.WriteLine("─── Test 2: process_read ───");
            
            try
            {
                // Start a process
                var startResult = await ProcessToolImpl.ProcessStartAsync(JsonSerializer.Serialize(new
                {
                    cmd = "dotnet",
                    args = new[] { "--version" }
                }));

                var startJson = JsonDocument.Parse(startResult);
                var sessionId = startJson.RootElement.GetProperty("session_id").GetString();
                
                // Wait for output
                await Task.Delay(1000);
                
                // Read output
                var readResult = await ProcessToolImpl.ProcessReadAsync(JsonSerializer.Serialize(new
                {
                    session_id = sessionId,
                    all = true
                }));

                var readJson = JsonDocument.Parse(readResult);
                var stdout = readJson.RootElement.GetProperty("stdout").GetString();
                var isRunning = readJson.RootElement.GetProperty("is_running").GetBoolean();
                
                Console.WriteLine($"  ✓ SUCCESS: Read output, is_running={isRunning}");
                Console.WriteLine($"    stdout: {stdout?.Trim()}");
                
                ProcessSessionManager.Instance.StopSession(sessionId!);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✗ EXCEPTION: {ex.Message}");
            }
            Console.WriteLine();
        }

        private static async Task Test3_ProcessStatusAsync()
        {
            Console.WriteLine("─── Test 3: process_status (list all) ───");
            
            try
            {
                // Start a couple of processes
                await ProcessToolImpl.ProcessStartAsync(JsonSerializer.Serialize(new
                {
                    cmd = "dotnet",
                    args = new[] { "--info" }
                }));

                // List all sessions
                var statusResult = await ProcessToolImpl.ProcessStatusAsync("{}");
                var json = JsonDocument.Parse(statusResult);
                var count = json.RootElement.GetProperty("session_count").GetInt32();
                
                Console.WriteLine($"  ✓ SUCCESS: Found {count} active session(s)");
                
                if (json.RootElement.TryGetProperty("sessions", out var sessions))
                {
                    foreach (var session in sessions.EnumerateArray())
                    {
                        var sid = session.GetProperty("session_id").GetString();
                        var cmd = session.GetProperty("command").GetString();
                        var running = session.GetProperty("is_running").GetBoolean();
                        Console.WriteLine($"    - {sid}: {cmd} (running={running})");
                    }
                }
                
                // Clean up
                ProcessSessionManager.Instance.StopAllSessions();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✗ EXCEPTION: {ex.Message}");
            }
            Console.WriteLine();
        }

        private static async Task Test4_ProcessWriteAsync()
        {
            Console.WriteLine("─── Test 4: process_write (skip - requires interactive process) ───");
            Console.WriteLine("  ⊘ SKIPPED: Test requires interactive process");
            Console.WriteLine();
            await Task.CompletedTask;
        }

        private static async Task Test5_ProcessStopAsync()
        {
            Console.WriteLine("─── Test 5: process_stop ───");
            
            try
            {
                // Start a long-running process (ping localhost)
                var startResult = await ProcessToolImpl.ProcessStartAsync(JsonSerializer.Serialize(new
                {
                    cmd = "dotnet",
                    args = new[] { "--info" }  // This takes a moment
                }));

                var startJson = JsonDocument.Parse(startResult);
                var sessionId = startJson.RootElement.GetProperty("session_id").GetString();
                
                // Give it a moment to start
                await Task.Delay(100);
                
                // Stop it
                var stopResult = await ProcessToolImpl.ProcessStopAsync(JsonSerializer.Serialize(new
                {
                    session_id = sessionId
                }));

                var stopJson = JsonDocument.Parse(stopResult);
                var success = stopJson.RootElement.GetProperty("success").GetBoolean();
                
                if (success)
                {
                    Console.WriteLine($"  ✓ SUCCESS: Stopped session {sessionId}");
                }
                else
                {
                    Console.WriteLine($"  ✗ FAILED: Could not stop session");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✗ EXCEPTION: {ex.Message}");
            }
            Console.WriteLine();
        }

        private static async Task Test6_ProcessWithOutputAsync()
        {
            Console.WriteLine("─── Test 6: Full workflow (start → wait → read → stop) ───");
            
            try
            {
                // Start dotnet --version
                var startResult = await ProcessToolImpl.ProcessStartAsync(JsonSerializer.Serialize(new
                {
                    cmd = "dotnet",
                    args = new[] { "--version" }
                }));

                var startJson = JsonDocument.Parse(startResult);
                var sessionId = startJson.RootElement.GetProperty("session_id").GetString();
                Console.WriteLine($"  1. Started: {sessionId}");
                
                // Wait for it to complete
                await Task.Delay(1500);
                
                // Read with wait
                var readResult = await ProcessToolImpl.ProcessReadAsync(JsonSerializer.Serialize(new
                {
                    session_id = sessionId,
                    all = true,
                    wait_ms = 500
                }));

                var readJson = JsonDocument.Parse(readResult);
                var stdout = readJson.RootElement.GetProperty("stdout").GetString();
                var isRunning = readJson.RootElement.GetProperty("is_running").GetBoolean();
                Console.WriteLine($"  2. Read output: is_running={isRunning}");
                Console.WriteLine($"     stdout: {stdout?.Trim()}");
                
                // Stop and get final output
                var stopResult = await ProcessToolImpl.ProcessStopAsync(JsonSerializer.Serialize(new
                {
                    session_id = sessionId
                }));

                var stopJson = JsonDocument.Parse(stopResult);
                var exitCode = stopJson.RootElement.TryGetProperty("exit_code", out var ec) ? ec.GetInt32() : -1;
                Console.WriteLine($"  3. Stopped: exit_code={exitCode}");
                
                Console.WriteLine($"  ✓ SUCCESS: Full workflow completed");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✗ EXCEPTION: {ex.Message}");
            }
            Console.WriteLine();
        }
    }
}
