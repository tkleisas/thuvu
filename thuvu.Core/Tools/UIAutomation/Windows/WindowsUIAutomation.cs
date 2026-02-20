using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using thuvu.Tools.UIAutomation.Models;

namespace thuvu.Tools.UIAutomation.Windows
{
    /// <summary>
    /// Windows UI Automation implementation using FlaUI library.
    /// Provides element tree inspection and intelligent element targeting.
    /// </summary>
    public class WindowsUIAutomation : IDisposable
    {
        private readonly UIA3Automation _automation;
        private bool _disposed;

        static WindowsUIAutomation()
        {
            // FlaUI.UIA3 requires the .NET Framework Accessibility.dll which .NET 8
            // won't probe for automatically. Resolve it from the app directory or
            // the .NET Framework install folder.
            AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
            {
                var name = new AssemblyName(args.Name);
                if (!name.Name.Equals("Accessibility", StringComparison.OrdinalIgnoreCase))
                    return null;

                // Try app directory first (copied by build)
                var local = Path.Combine(AppContext.BaseDirectory, "Accessibility.dll");
                if (File.Exists(local))
                    return Assembly.LoadFrom(local);

                // Fallback to .NET Framework directory
                var fw = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    @"Microsoft.NET\Framework64\v4.0.30319\Accessibility.dll");
                if (File.Exists(fw))
                    return Assembly.LoadFrom(fw);

                return null;
            };
        }
        
        public WindowsUIAutomation()
        {
            _automation = new UIA3Automation();
        }
        
        /// <summary>
        /// Get UI element at screen coordinates
        /// </summary>
        public UIElement? GetElementAt(int x, int y)
        {
            try
            {
                var element = _automation.FromPoint(new Point(x, y));
                return element != null ? ConvertElement(element) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
        
        /// <summary>
        /// Get the currently focused UI element
        /// </summary>
        public UIElement? GetFocusedElement()
        {
            try
            {
                var element = _automation.FocusedElement();
                return element != null ? ConvertElement(element) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
        
        /// <summary>
        /// Find elements by selector within a window
        /// Selector format: "ControlType:Name" or just "Name" or "ControlType:*"
        /// Examples: "Button:Submit", "TextBox:Username", "Button:*", "OK"
        /// </summary>
        public List<UIElement> FindElements(string selector, string? windowTitle = null)
        {
            var results = new List<UIElement>();
            
            try
            {
                // Get the root element (desktop or specific window)
                AutomationElement root;
                if (!string.IsNullOrEmpty(windowTitle))
                {
                    var window = FindWindowByTitle(windowTitle);
                    if (window == null)
                        return results;
                    root = window;
                }
                else
                {
                    root = _automation.GetDesktop();
                }
                
                // Parse selector
                var (controlType, name) = ParseSelector(selector);
                
                // Build condition
                var cf = _automation.ConditionFactory;
                ConditionBase condition;
                
                if (controlType != null && name != null && name != "*")
                {
                    // Use AndCondition constructor instead of factory method
                    condition = new AndCondition(
                        cf.ByControlType(controlType.Value),
                        cf.ByName(name)
                    );
                }
                else if (controlType != null)
                {
                    condition = cf.ByControlType(controlType.Value);
                }
                else if (name != null)
                {
                    condition = cf.ByName(name);
                }
                else
                {
                    return results;
                }
                
                // Find elements
                var elements = root.FindAllDescendants(condition);
                
                foreach (var elem in elements.Take(50)) // Limit results
                {
                    var uiElem = ConvertElement(elem);
                    if (uiElem != null)
                        results.Add(uiElem);
                }
            }
            catch (Exception)
            {
                // Ignore errors during element search
            }
            
            return results;
        }
        
        /// <summary>
        /// Find a window by title (partial match)
        /// </summary>
        public AutomationElement? FindWindowByTitle(string titlePattern)
        {
            try
            {
                var desktop = _automation.GetDesktop();
                var windows = desktop.FindAllChildren(_automation.ConditionFactory.ByControlType(ControlType.Window));
                
                foreach (var window in windows)
                {
                    try
                    {
                        var name = window.Properties.Name.ValueOrDefault;
                        if (!string.IsNullOrEmpty(name) && 
                            name.Contains(titlePattern, StringComparison.OrdinalIgnoreCase))
                        {
                            return window;
                        }
                    }
                    catch
                    {
                        // Skip inaccessible windows
                    }
                }
            }
            catch
            {
                // Ignore errors
            }
            
            return null;
        }
        
        /// <summary>
        /// Get element tree for a window (limited depth)
        /// </summary>
        public UIElement? GetWindowElementTree(string windowTitle, int maxDepth = 3)
        {
            var window = FindWindowByTitle(windowTitle);
            if (window == null)
                return null;
            
            return ConvertElementWithChildren(window, maxDepth, 0);
        }
        
        /// <summary>
        /// Wait for an element to appear
        /// </summary>
        public UIElement? WaitForElement(string selector, string? windowTitle, int timeoutMs)
        {
            var startTime = DateTime.Now;
            var timeout = TimeSpan.FromMilliseconds(timeoutMs);
            
            while (DateTime.Now - startTime < timeout)
            {
                var elements = FindElements(selector, windowTitle);
                if (elements.Count > 0)
                    return elements[0];
                
                System.Threading.Thread.Sleep(100);
            }
            
            return null;
        }
        
        /// <summary>
        /// Wait for a window to appear
        /// </summary>
        public bool WaitForWindow(string titlePattern, int timeoutMs)
        {
            var startTime = DateTime.Now;
            var timeout = TimeSpan.FromMilliseconds(timeoutMs);
            
            while (DateTime.Now - startTime < timeout)
            {
                var window = FindWindowByTitle(titlePattern);
                if (window != null)
                    return true;
                
                System.Threading.Thread.Sleep(100);
            }
            
            return false;
        }
        
        /// <summary>
        /// Parse selector string into control type and name
        /// </summary>
        private (ControlType? controlType, string? name) ParseSelector(string selector)
        {
            if (string.IsNullOrWhiteSpace(selector))
                return (null, null);
            
            // Check for "ControlType:Name" format
            var colonIndex = selector.IndexOf(':');
            if (colonIndex > 0)
            {
                var typePart = selector.Substring(0, colonIndex).Trim();
                var namePart = selector.Substring(colonIndex + 1).Trim();
                
                var controlType = ParseControlType(typePart);
                return (controlType, string.IsNullOrEmpty(namePart) ? null : namePart);
            }
            
            // Try to parse as control type first
            var ct = ParseControlType(selector);
            if (ct != null)
                return (ct, null);
            
            // Otherwise treat as name
            return (null, selector);
        }
        
        /// <summary>
        /// Parse control type string
        /// </summary>
        private ControlType? ParseControlType(string typeName)
        {
            return typeName.ToLowerInvariant() switch
            {
                "button" => ControlType.Button,
                "text" or "textbox" or "edit" => ControlType.Edit,
                "checkbox" or "check" => ControlType.CheckBox,
                "radiobutton" or "radio" => ControlType.RadioButton,
                "combobox" or "combo" or "dropdown" => ControlType.ComboBox,
                "list" or "listbox" => ControlType.List,
                "listitem" => ControlType.ListItem,
                "menu" => ControlType.Menu,
                "menuitem" => ControlType.MenuItem,
                "tab" or "tabitem" => ControlType.TabItem,
                "tree" or "treeview" => ControlType.Tree,
                "treeitem" => ControlType.TreeItem,
                "window" => ControlType.Window,
                "pane" => ControlType.Pane,
                "document" => ControlType.Document,
                "hyperlink" or "link" => ControlType.Hyperlink,
                "image" => ControlType.Image,
                "slider" => ControlType.Slider,
                "spinner" => ControlType.Spinner,
                "statusbar" => ControlType.StatusBar,
                "toolbar" => ControlType.ToolBar,
                "tooltip" => ControlType.ToolTip,
                "progressbar" or "progress" => ControlType.ProgressBar,
                "scrollbar" => ControlType.ScrollBar,
                "table" or "grid" => ControlType.Table,
                "dataitem" or "row" => ControlType.DataItem,
                "header" => ControlType.Header,
                "headeritem" => ControlType.HeaderItem,
                "group" => ControlType.Group,
                "thumb" => ControlType.Thumb,
                "datagrid" => ControlType.DataGrid,
                "calendar" => ControlType.Calendar,
                "splitbutton" => ControlType.SplitButton,
                "separator" => ControlType.Separator,
                "titlebar" => ControlType.TitleBar,
                _ => null
            };
        }
        
        /// <summary>
        /// Convert FlaUI element to our UIElement model
        /// </summary>
        private UIElement? ConvertElement(AutomationElement element)
        {
            try
            {
                var rect = element.BoundingRectangle;
                
                return new UIElement
                {
                    AutomationId = element.Properties.AutomationId.ValueOrDefault ?? "",
                    Name = element.Properties.Name.ValueOrDefault ?? "",
                    ClassName = element.Properties.ClassName.ValueOrDefault ?? "",
                    ControlType = element.Properties.ControlType.ValueOrDefault.ToString(),
                    X = (int)rect.X,
                    Y = (int)rect.Y,
                    Width = (int)rect.Width,
                    Height = (int)rect.Height,
                    IsEnabled = element.Properties.IsEnabled.ValueOrDefault,
                    IsVisible = !element.Properties.IsOffscreen.ValueOrDefault,
                    Value = GetElementValue(element)
                };
            }
            catch
            {
                return null;
            }
        }
        
        /// <summary>
        /// Convert element with children (for tree view)
        /// </summary>
        private UIElement? ConvertElementWithChildren(AutomationElement element, int maxDepth, int currentDepth)
        {
            var uiElement = ConvertElement(element);
            if (uiElement == null || currentDepth >= maxDepth)
                return uiElement;
            
            try
            {
                var children = element.FindAllChildren();
                foreach (var child in children.Take(20)) // Limit children per level
                {
                    var childElement = ConvertElementWithChildren(child, maxDepth, currentDepth + 1);
                    if (childElement != null)
                        uiElement.Children.Add(childElement);
                }
            }
            catch
            {
                // Ignore errors getting children
            }
            
            return uiElement;
        }
        
        /// <summary>
        /// Get the value of an element (for text boxes, etc.)
        /// </summary>
        private string? GetElementValue(AutomationElement element)
        {
            try
            {
                // Try value pattern first
                if (element.Patterns.Value.IsSupported)
                {
                    return element.Patterns.Value.Pattern.Value.Value;
                }
                
                // Try text pattern
                if (element.Patterns.Text.IsSupported)
                {
                    return element.Patterns.Text.Pattern.DocumentRange.GetText(-1);
                }
                
                // Try toggle pattern for checkboxes
                if (element.Patterns.Toggle.IsSupported)
                {
                    return element.Patterns.Toggle.Pattern.ToggleState.Value.ToString();
                }
                
                // Try selection pattern for combo boxes
                if (element.Patterns.Selection.IsSupported)
                {
                    var selection = element.Patterns.Selection.Pattern.Selection.Value;
                    if (selection.Length > 0)
                    {
                        return selection[0].Properties.Name.ValueOrDefault;
                    }
                }
            }
            catch
            {
                // Ignore errors getting value
            }
            
            return null;
        }
        
        public void Dispose()
        {
            if (!_disposed)
            {
                _automation?.Dispose();
                _disposed = true;
            }
        }
    }
}
