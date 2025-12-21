using System;
using System.Threading;
using Terminal.Gui;
using thuvu.Models;
using TgAttribute = Terminal.Gui.Attribute;

namespace thuvu.Tui
{
    /// <summary>
    /// TUI-compatible permission prompt using Dialog
    /// </summary>
    public static class TuiPermissionDialog
    {
        /// <summary>
        /// Show a permission prompt dialog and return the user's choice
        /// </summary>
        public static char Show(string toolName, string argsJson, Action<string>? onResult = null)
        {
            char result = 'N'; // Default to deny
            
            var completionEvent = new ManualResetEventSlim(false);
            var timeoutSeconds = 300; // 5 minute timeout for user response
            
            Application.Invoke(() =>
            {
                try
                {
                    Application.Wakeup();
                    
                    // Create buttons
                    var alwaysBtn = new Button { Text = "_Always" };
                    var sessionBtn = new Button { Text = "_Session" };
                    var onceBtn = new Button { Text = "_Once" };
                    var noBtn = new Button { Text = "_No" };
                    
                    // Create dialog with buttons
                    var dialog = new Dialog
                    {
                        Title = "⚠️ Permission Required",
                        Width = 65,
                        Height = 14,
                        Buttons = [alwaysBtn, sessionBtn, onceBtn, noBtn]
                    };
                    
                    // Add content labels
                    var toolLabel = new Label
                    {
                        X = 1,
                        Y = 1,
                        Text = $"Tool: {toolName}"
                    };
                    
                    var argsDisplay = argsJson.Length > 50 ? argsJson.Substring(0, 47) + "..." : argsJson;
                    var argsLabel = new Label
                    {
                        X = 1,
                        Y = 3,
                        Width = Dim.Fill() - 2,
                        Text = $"Args: {argsDisplay}"
                    };
                    
                    var questionLabel = new Label
                    {
                        X = 1,
                        Y = 5,
                        Text = "Allow this operation?"
                    };
                    
                    var hintLabel = new Label
                    {
                        X = 1,
                        Y = 7,
                        ColorScheme = new ColorScheme { Normal = new TgAttribute(Color.DarkGray, Color.Black) },
                        Text = "[A]lways=persist | [S]ession=temp | [O]nce | [N]o=deny"
                    };
                    
                    dialog.Add(toolLabel, argsLabel, questionLabel, hintLabel);
                    
                    // Button handlers
                    alwaysBtn.Accepting += (s, e) => { result = 'A'; Application.RequestStop(dialog); };
                    sessionBtn.Accepting += (s, e) => { result = 'S'; Application.RequestStop(dialog); };
                    onceBtn.Accepting += (s, e) => { result = 'O'; Application.RequestStop(dialog); };
                    noBtn.Accepting += (s, e) => { result = 'N'; Application.RequestStop(dialog); };
                    
                    // Handle ESC key to deny
                    dialog.KeyDown += (s, e) =>
                    {
                        if (e.KeyCode == Key.Esc)
                        {
                            result = 'N';
                            Application.RequestStop(dialog);
                            e.Handled = true;
                        }
                    };
                    
                    // Run the dialog modally
                    Application.Run(dialog);
                    dialog.Dispose();
                }
                catch (Exception ex)
                {
                    SessionLogger.Instance.LogError($"Permission dialog error: {ex.Message}");
                    result = 'N';
                }
                finally
                {
                    completionEvent.Set();
                }
            });
            
            // Force a UI refresh
            Application.Wakeup();
            
            // Wait for dialog to complete with timeout
            if (!completionEvent.Wait(TimeSpan.FromSeconds(timeoutSeconds)))
            {
                SessionLogger.Instance.LogInfo($"Permission prompt timed out after {timeoutSeconds}s for tool: {toolName} - denying");
                Application.Invoke(() => Application.RequestStop());
                result = 'N';
            }
            
            // Log the result
            var action = result switch
            {
                'A' => "Always allowed",
                'S' => "Session allowed", 
                'O' => "Once allowed",
                _ => "Denied"
            };
            SessionLogger.Instance.LogInfo($"Permission {action} for tool: {toolName}");
            
            onResult?.Invoke(action);
            
            return result;
        }
    }
}
