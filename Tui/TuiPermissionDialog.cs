using System;
using System.Threading;
using Terminal.Gui.Views;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using TgAttr = Terminal.Gui.Drawing.Attribute;
using thuvu.Models;

namespace thuvu.Tui
{
    public static class TuiPermissionDialog
    {
        public static char Show(string toolName, string argsJson, Action<string>? onResult = null)
        {
            char result = 'N';
            
            var completionEvent = new ManualResetEventSlim(false);
            var timeoutSeconds = 300;
            
            Application.Invoke(() =>
            {
                try
                {
                    var alwaysBtn = new Button { Text = "_Always" };
                    var sessionBtn = new Button { Text = "_Session" };
                    var onceBtn = new Button { Text = "_Once" };
                    var noBtn = new Button { Text = "_No" };
                    
                    var dialog = new Dialog
                    {
                        Title = "Permission Required",
                        Width = 65,
                        Height = 14,
                        Buttons = [alwaysBtn, sessionBtn, onceBtn, noBtn]
                    };
                    
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
                        Text = "[A]lways=persist | [S]ession=temp | [O]nce | [N]o=deny"
                    };
                    hintLabel.SetScheme(new Scheme { Normal = new TgAttr(Color.DarkGray, Color.Black) });
                    
                    dialog.Add(toolLabel, argsLabel, questionLabel, hintLabel);
                    
                    alwaysBtn.Accepting += (s, e) => { result = 'A'; Application.RequestStop(dialog); };
                    sessionBtn.Accepting += (s, e) => { result = 'S'; Application.RequestStop(dialog); };
                    onceBtn.Accepting += (s, e) => { result = 'O'; Application.RequestStop(dialog); };
                    noBtn.Accepting += (s, e) => { result = 'N'; Application.RequestStop(dialog); };
                    
                    dialog.KeyDown += (s, e) =>
                    {
                        if (e.KeyCode == KeyCode.Esc)
                        {
                            result = 'N';
                            Application.RequestStop(dialog);
                            e.Handled = true;
                        }
                    };
                    
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
            
            if (!completionEvent.Wait(TimeSpan.FromSeconds(timeoutSeconds)))
            {
                SessionLogger.Instance.LogInfo($"Permission prompt timed out after {timeoutSeconds}s for tool: {toolName} - denying");
                Application.Invoke(() => Application.RequestStop());
                result = 'N';
            }
            
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
