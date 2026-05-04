using Terminal.Gui.Drawing;
using TgAttr = Terminal.Gui.Drawing.Attribute;

namespace thuvu.Tui
{
    public static class TuiStyles
    {
        public static Scheme StatusBar => new()
        {
            Normal = new TgAttr(Color.Green, Color.Black)
        };
        
        public static Scheme ActionView => new()
        {
            Normal = new TgAttr(Color.White, Color.Black),
            Focus = new TgAttr(Color.BrightYellow, Color.Black)
        };
        
        public static Scheme CommandLabel => new()
        {
            Normal = new TgAttr(Color.DarkGray, Color.Black)
        };
        
        public static Scheme WorkLabel => new()
        {
            Normal = new TgAttr(Color.Cyan, Color.Black)
        };
        
        public static Scheme CommandField => new()
        {
            Normal = new TgAttr(Color.BrightYellow, Color.Black),
            Focus = new TgAttr(Color.BrightYellow, Color.DarkGray)
        };
        
        public static Scheme AutocompleteFrame => new()
        {
            Normal = new TgAttr(Color.Black, Color.Gray),
            Focus = new TgAttr(Color.Black, Color.Gray)
        };
        
        public static Scheme AutocompleteList => new()
        {
            Normal = new TgAttr(Color.Black, Color.Gray),
            Focus = new TgAttr(Color.White, Color.Blue)
        };
        
        public static Scheme OrchestratorFrame => new()
        {
            Normal = new TgAttr(Color.Cyan, Color.Black),
            Focus = new TgAttr(Color.Cyan, Color.Black)
        };
        
        public static Scheme AgentFrame => new()
        {
            Normal = new TgAttr(Color.Green, Color.Black),
            Focus = new TgAttr(Color.Green, Color.Black)
        };
        
        public static Scheme AgentView => new()
        {
            Normal = new TgAttr(Color.White, Color.Black),
            Focus = new TgAttr(Color.BrightYellow, Color.Black)
        };
        
        public static Scheme DimText => new()
        {
            Normal = new TgAttr(Color.DarkGray, Color.Black)
        };
        
        public const string Banner = 
            "╔══════════════════════════════════════════════════════════════╗\n"+
            "║  T.H.U.V.U. - Tool for Heuristic Universal Versatile Usage   ║\n"+
            "╚══════════════════════════════════════════════════════════════╝\n";
        
        public const string WelcomeMessage = "Welcome! Type commands or chat. Ctrl+Enter to send. /help for commands.\n\n";
    }
}
