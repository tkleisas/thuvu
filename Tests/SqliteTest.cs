using System;
using System.IO;
using System.Threading.Tasks;
using thuvu.Models;
using thuvu.Tools;

namespace thuvu.Tests
{
    /// <summary>
    /// Test suite for SQLite code indexing and context storage.
    /// Run with: dotnet run -- --test-sqlite
    /// </summary>
    public static class SqliteTest
    {
        public static async Task<int> RunTests()
        {
            Console.WriteLine("=== SQLite Code Indexing Tests ===\n");
            
            int passed = 0;
            int failed = 0;
            
            // Initialize
            SqliteConfig.LoadConfig();
            
            // Test 1: Database initialization
            Console.Write("Test 1: Database initialization... ");
            try
            {
                await SqliteService.Instance.InitializeAsync();
                Console.WriteLine("PASSED");
                passed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAILED: {ex.Message}");
                failed++;
            }
            
            // Test 2: Index stats (empty)
            Console.Write("Test 2: Index stats (empty)... ");
            try
            {
                var result = await SqliteToolImpl.IndexStatsAsync();
                if (result.Contains("\"success\":true"))
                {
                    Console.WriteLine("PASSED");
                    passed++;
                }
                else
                {
                    Console.WriteLine($"FAILED: {result}");
                    failed++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAILED: {ex.Message}");
                failed++;
            }
            
            // Test 3: Index a C# file
            Console.Write("Test 3: Index a C# file... ");
            try
            {
                // Create a test file
                var testDir = Path.Combine(Path.GetTempPath(), "thuvu_test_" + Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(testDir);
                var testFile = Path.Combine(testDir, "TestClass.cs");
                File.WriteAllText(testFile, @"
namespace TestNamespace
{
    /// <summary>
    /// A test class for indexing.
    /// </summary>
    public class TestClass
    {
        private int _counter;
        
        public string Name { get; set; }
        
        public void DoSomething(int value)
        {
            _counter = value;
        }
        
        public int GetCounter() => _counter;
    }
    
    public interface ITestInterface
    {
        void Process();
    }
    
    public enum TestEnum
    {
        Value1,
        Value2,
        Value3
    }
}
");
                
                var result = await SqliteToolImpl.CodeIndexAsync(testFile, true);
                if (result.Contains("\"success\":true") && result.Contains("\"indexed\":true"))
                {
                    Console.WriteLine("PASSED");
                    passed++;
                }
                else
                {
                    Console.WriteLine($"FAILED: {result}");
                    failed++;
                }
                
                // Cleanup test file (but keep for query tests)
                _testDir = testDir;
                _testFile = testFile;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAILED: {ex.Message}");
                failed++;
            }
            
            // Test 4: Query symbols by name
            Console.Write("Test 4: Query symbols by name... ");
            try
            {
                var result = await SqliteToolImpl.CodeQueryAsync(search: "TestClass");
                if (result.Contains("\"success\":true") && result.Contains("\"name\":\"TestClass\""))
                {
                    Console.WriteLine("PASSED");
                    passed++;
                }
                else
                {
                    Console.WriteLine($"FAILED: {result}");
                    failed++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAILED: {ex.Message}");
                failed++;
            }
            
            // Test 5: Query by kind
            Console.Write("Test 5: Query symbols by kind (method)... ");
            try
            {
                var result = await SqliteToolImpl.CodeQueryAsync(search: "DoSomething", kind: "method");
                if (result.Contains("\"success\":true") && result.Contains("\"kind\":\"method\""))
                {
                    Console.WriteLine("PASSED");
                    passed++;
                }
                else
                {
                    Console.WriteLine($"FAILED: {result}");
                    failed++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAILED: {ex.Message}");
                failed++;
            }
            
            // Test 6: Query symbols in file
            Console.Write("Test 6: Query symbols in file... ");
            try
            {
                if (_testFile != null)
                {
                    var result = await SqliteToolImpl.CodeQueryAsync(file: _testFile);
                    // Should find: TestClass, Name, _counter, DoSomething, GetCounter, ITestInterface, Process, TestEnum, Value1-3
                    if (result.Contains("\"success\":true") && result.Contains("\"count\":"))
                    {
                        Console.WriteLine("PASSED");
                        passed++;
                    }
                    else
                    {
                        Console.WriteLine($"FAILED: {result}");
                        failed++;
                    }
                }
                else
                {
                    Console.WriteLine("SKIPPED (no test file)");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAILED: {ex.Message}");
                failed++;
            }
            
            // Test 7: Store context
            Console.Write("Test 7: Store context... ");
            try
            {
                var result = await SqliteToolImpl.ContextStoreAsync(
                    key: "test_decision_1",
                    value: "Use dependency injection for services",
                    category: "decision",
                    projectPath: "/test/project");
                    
                if (result.Contains("\"success\":true") && result.Contains("\"id\":"))
                {
                    Console.WriteLine("PASSED");
                    passed++;
                }
                else
                {
                    Console.WriteLine($"FAILED: {result}");
                    failed++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAILED: {ex.Message}");
                failed++;
            }
            
            // Test 8: Get context
            Console.Write("Test 8: Get context... ");
            try
            {
                var result = await SqliteToolImpl.ContextGetAsync(keyPattern: "test_decision");
                if (result.Contains("\"success\":true") && result.Contains("Use dependency injection"))
                {
                    Console.WriteLine("PASSED");
                    passed++;
                }
                else
                {
                    Console.WriteLine($"FAILED: {result}");
                    failed++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAILED: {ex.Message}");
                failed++;
            }
            
            // Test 9: Get context by category
            Console.Write("Test 9: Get context by category... ");
            try
            {
                var result = await SqliteToolImpl.ContextGetAsync(category: "decision");
                if (result.Contains("\"success\":true") && result.Contains("\"category\":\"decision\""))
                {
                    Console.WriteLine("PASSED");
                    passed++;
                }
                else
                {
                    Console.WriteLine($"FAILED: {result}");
                    failed++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAILED: {ex.Message}");
                failed++;
            }
            
            // Test 10: Index stats (with data)
            Console.Write("Test 10: Index stats (with data)... ");
            try
            {
                var result = await SqliteToolImpl.IndexStatsAsync();
                if (result.Contains("\"success\":true") && 
                    result.Contains("\"totalSymbols\":") && 
                    result.Contains("\"totalContextEntries\":"))
                {
                    Console.WriteLine("PASSED");
                    passed++;
                }
                else
                {
                    Console.WriteLine($"FAILED: {result}");
                    failed++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAILED: {ex.Message}");
                failed++;
            }
            
            // Test 11: Incremental indexing (skip unchanged)
            Console.Write("Test 11: Incremental indexing (skip unchanged)... ");
            try
            {
                if (_testFile != null)
                {
                    var result = await SqliteToolImpl.CodeIndexAsync(_testFile, force: false);
                    if (result.Contains("\"success\":true") && result.Contains("\"indexed\":false"))
                    {
                        Console.WriteLine("PASSED");
                        passed++;
                    }
                    else
                    {
                        Console.WriteLine($"FAILED: {result}");
                        failed++;
                    }
                }
                else
                {
                    Console.WriteLine("SKIPPED (no test file)");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAILED: {ex.Message}");
                failed++;
            }
            
            // Test 12: Python parser
            Console.Write("Test 12: Index Python file... ");
            string? pyTestDir = null;
            try
            {
                pyTestDir = Path.Combine(Path.GetTempPath(), "thuvu_py_" + Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(pyTestDir);
                var pyFile = Path.Combine(pyTestDir, "sample.py");
                File.WriteAllText(pyFile, @"
def hello():
    '''Greeting function'''
    print('hello')

class Person:
    """"""Represents a person""""""
    def __init__(self, name):
        self.name = name
    
    def greet(self):
        return 'Hello!'
");
                var result = await SqliteToolImpl.CodeIndexAsync(pyFile, force: true);
                if (result.Contains("\"success\":true") && result.Contains("\"indexed\":true"))
                {
                    // Verify symbols were found
                    var queryResult = await SqliteToolImpl.CodeQueryAsync(search: "hello");
                    if (queryResult.Contains("\"kind\":\"function\""))
                    {
                        Console.WriteLine("PASSED");
                        passed++;
                    }
                    else
                    {
                        Console.WriteLine($"FAILED: Python symbols not found. Query: {queryResult}");
                        failed++;
                    }
                }
                else
                {
                    Console.WriteLine($"FAILED: {result}");
                    failed++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAILED: {ex.Message}");
                failed++;
            }
            finally
            {
                if (pyTestDir != null && Directory.Exists(pyTestDir))
                    Directory.Delete(pyTestDir, true);
            }
            
            // Test 13: TypeScript parser
            Console.Write("Test 13: Index TypeScript file... ");
            string? tsTestDir = null;
            try
            {
                tsTestDir = Path.Combine(Path.GetTempPath(), "thuvu_ts_" + Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(tsTestDir);
                var tsFile = Path.Combine(tsTestDir, "sample.ts");
                File.WriteAllText(tsFile, @"
interface User {
    id: number;
    name: string;
}

class UserService {
    getUser(id: number): User | undefined {
        return undefined;
    }
}

function greet(name: string): string {
    return 'Hello!';
}

enum Role { Admin, User }
");
                var result = await SqliteToolImpl.CodeIndexAsync(tsFile, force: true);
                if (result.Contains("\"success\":true") && result.Contains("\"indexed\":true"))
                {
                    var queryResult = await SqliteToolImpl.CodeQueryAsync(search: "UserService");
                    if (queryResult.Contains("\"kind\":\"class\""))
                    {
                        Console.WriteLine("PASSED");
                        passed++;
                    }
                    else
                    {
                        Console.WriteLine($"FAILED: TypeScript class not found. Query: {queryResult}");
                        failed++;
                    }
                }
                else
                {
                    Console.WriteLine($"FAILED: {result}");
                    failed++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAILED: {ex.Message}");
                failed++;
            }
            finally
            {
                if (tsTestDir != null && Directory.Exists(tsTestDir))
                    Directory.Delete(tsTestDir, true);
            }
            
            // Test 14: Go parser
            Console.Write("Test 14: Index Go file... ");
            string? goTestDir = null;
            try
            {
                goTestDir = Path.Combine(Path.GetTempPath(), "thuvu_go_" + Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(goTestDir);
                var goFile = Path.Combine(goTestDir, "sample.go");
                File.WriteAllText(goFile, @"
package main

// User represents a user
type User struct {
    ID   int
    Name string
}

// Greet returns a greeting
func (u *User) Greet() string {
    return ""Hello""
}

func NewUser(name string) *User {
    return &User{Name: name}
}
");
                var result = await SqliteToolImpl.CodeIndexAsync(goFile, force: true);
                if (result.Contains("\"success\":true") && result.Contains("\"indexed\":true"))
                {
                    var queryResult = await SqliteToolImpl.CodeQueryAsync(search: "NewUser");
                    if (queryResult.Contains("\"kind\":\"function\""))
                    {
                        Console.WriteLine("PASSED");
                        passed++;
                    }
                    else
                    {
                        Console.WriteLine($"FAILED: Go function not found. Query: {queryResult}");
                        failed++;
                    }
                }
                else
                {
                    Console.WriteLine($"FAILED: {result}");
                    failed++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAILED: {ex.Message}");
                failed++;
            }
            finally
            {
                if (goTestDir != null && Directory.Exists(goTestDir))
                    Directory.Delete(goTestDir, true);
            }
            
            // Cleanup
            Console.Write("\nCleaning up... ");
            try
            {
                await SqliteToolImpl.IndexClearAsync();
                if (_testDir != null && Directory.Exists(_testDir))
                {
                    Directory.Delete(_testDir, true);
                }
                Console.WriteLine("Done");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: {ex.Message}");
            }
            
            // Summary
            Console.WriteLine($"\n=== Results: {passed} passed, {failed} failed ===");
            return failed;
        }
        
        private static string? _testDir;
        private static string? _testFile;
    }
}
