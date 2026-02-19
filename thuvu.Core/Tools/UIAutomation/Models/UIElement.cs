using System.Collections.Generic;

namespace thuvu.Tools.UIAutomation.Models
{
    /// <summary>
    /// Represents a UI element from the automation tree
    /// </summary>
    public class UIElement
    {
        /// <summary>
        /// Automation ID of the element
        /// </summary>
        public string AutomationId { get; set; } = "";
        
        /// <summary>
        /// Display name of the element
        /// </summary>
        public string Name { get; set; } = "";
        
        /// <summary>
        /// Window class name
        /// </summary>
        public string ClassName { get; set; } = "";
        
        /// <summary>
        /// Control type (Button, TextBox, etc.)
        /// </summary>
        public string ControlType { get; set; } = "";
        
        /// <summary>
        /// Element left position (screen coordinates)
        /// </summary>
        public int X { get; set; }
        
        /// <summary>
        /// Element top position (screen coordinates)
        /// </summary>
        public int Y { get; set; }
        
        /// <summary>
        /// Element width in pixels
        /// </summary>
        public int Width { get; set; }
        
        /// <summary>
        /// Element height in pixels
        /// </summary>
        public int Height { get; set; }
        
        /// <summary>
        /// Whether the element is enabled
        /// </summary>
        public bool IsEnabled { get; set; }
        
        /// <summary>
        /// Whether the element is visible on screen
        /// </summary>
        public bool IsVisible { get; set; }
        
        /// <summary>
        /// Current value (for text fields, checkboxes, etc.)
        /// </summary>
        public string? Value { get; set; }
        
        /// <summary>
        /// Child elements
        /// </summary>
        public List<UIElement> Children { get; set; } = new();
    }
}
