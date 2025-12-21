using System;
using Terminal.Gui;
using TgAttribute = Terminal.Gui.Attribute;

namespace thuvu.Tui
{
    /// <summary>
    /// UI color schemes and styling for the TUI
    /// </summary>
    public static class TuiStyles
    {
        public static ColorScheme StatusBar => new()
        {
            Normal = new TgAttribute(Color.Green, Color.Black)
        };
        
        public static ColorScheme ActionView => new()
        {
            Normal = new TgAttribute(Color.White, Color.Black),
            Focus = new TgAttribute(Color.BrightYellow, Color.Black)
        };
        
        public static ColorScheme CommandLabel => new()
        {
            Normal = new TgAttribute(Color.DarkGray, Color.Black)
        };
        
        public static ColorScheme WorkLabel => new()
        {
            Normal = new TgAttribute(Color.Cyan, Color.Black)
        };
        
        public static ColorScheme CommandField => new()
        {
            Normal = new TgAttribute(Color.BrightYellow, Color.Black),
            Focus = new TgAttribute(Color.BrightYellow, Color.DarkGray)
        };
        
        public static ColorScheme AutocompleteFrame => new()
        {
            Normal = new TgAttribute(Color.Black, Color.Gray),
            Focus = new TgAttribute(Color.Black, Color.Gray)
        };
        
        public static ColorScheme AutocompleteList => new()
        {
            Normal = new TgAttribute(Color.Black, Color.Gray),
            Focus = new TgAttribute(Color.White, Color.Blue)
        };
        
        public static ColorScheme OrchestratorFrame => new()
        {
            Normal = new TgAttribute(Color.Cyan, Color.Black),
            Focus = new TgAttribute(Color.Cyan, Color.Black)
        };
        
        public static ColorScheme AgentFrame => new()
        {
            Normal = new TgAttribute(Color.Green, Color.Black),
            Focus = new TgAttribute(Color.Green, Color.Black)
        };
        
        public static ColorScheme AgentView => new()
        {
            Normal = new TgAttribute(Color.White, Color.Black),
            Focus = new TgAttribute(Color.BrightYellow, Color.Black)
        };
        
        public static ColorScheme DimText => new()
        {
            Normal = new TgAttribute(Color.DarkGray, Color.Black)
        };
        
        public const string Banner = 
            "╔══════════════════════════════════════════════════════════════╗\n"+
            "║  T.H.U.V.U. - Tool for Heuristic Universal Versatile Usage   ║\n"+
            "╚══════════════════════════════════════════════════════════════╝\n";
        
        public const string WelcomeMessage = "Welcome! Type commands or chat. Ctrl+Enter to send. /help for commands.\n\n";
    }
}
