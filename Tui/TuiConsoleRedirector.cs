using System;
using System.IO;
using System.Text;

namespace thuvu.Tui
{
    /// <summary>
    /// Redirects Console.Write/WriteLine to a callback when TUI is active.
    /// This prevents raw console output from corrupting the Terminal.Gui display.
    /// </summary>
    public class TuiConsoleRedirector : TextWriter
    {
        private readonly TextWriter _originalOut;
        private readonly TextWriter _originalError;
        private readonly Action<string> _outputCallback;
        private readonly StringBuilder _buffer = new();
        private bool _isActive;
        
        private static TuiConsoleRedirector? _instance;
        public static bool IsRedirecting => _instance?._isActive == true;
        
        public override Encoding Encoding => Encoding.UTF8;
        
        private TuiConsoleRedirector(Action<string> outputCallback)
        {
            _originalOut = Console.Out;
            _originalError = Console.Error;
            _outputCallback = outputCallback;
        }
        
        /// <summary>
        /// Start redirecting console output to the callback
        /// </summary>
        public static void StartRedirection(Action<string> outputCallback)
        {
            if (_instance != null)
            {
                StopRedirection();
            }
            
            _instance = new TuiConsoleRedirector(outputCallback);
            _instance._isActive = true;
            Console.SetOut(_instance);
            Console.SetError(_instance);
        }
        
        /// <summary>
        /// Stop redirecting and restore original console output
        /// </summary>
        public static void StopRedirection()
        {
            if (_instance == null) return;
            
            _instance._isActive = false;
            Console.SetOut(_instance._originalOut);
            Console.SetError(_instance._originalError);
            _instance = null;
        }
        
        public override void Write(char value)
        {
            if (!_isActive)
            {
                _originalOut.Write(value);
                return;
            }
            
            // Buffer the character
            _buffer.Append(value);
            
            // Flush on newline
            if (value == '\n')
            {
                FlushBuffer();
            }
        }
        
        public override void Write(string? value)
        {
            if (value == null) return;
            
            if (!_isActive)
            {
                _originalOut.Write(value);
                return;
            }
            
            _buffer.Append(value);
            
            // Flush if contains newline
            if (value.Contains('\n'))
            {
                FlushBuffer();
            }
        }
        
        public override void WriteLine(string? value)
        {
            Write(value);
            Write('\n');
        }
        
        public override void WriteLine()
        {
            Write('\n');
        }
        
        public override void Flush()
        {
            FlushBuffer();
            _originalOut.Flush();
        }
        
        private void FlushBuffer()
        {
            if (_buffer.Length == 0) return;
            
            var text = _buffer.ToString();
            _buffer.Clear();
            
            try
            {
                _outputCallback(text);
            }
            catch
            {
                // Ignore callback errors to prevent infinite loops
            }
        }
        
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                FlushBuffer();
                if (_isActive)
                {
                    StopRedirection();
                }
            }
            base.Dispose(disposing);
        }
    }
}
