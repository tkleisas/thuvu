// T.H.U.V.U. Web Interface JavaScript Interop

// Visibility handler reference
let visibilityDotNetRef = null;

// Setup visibility change handler for background tab detection
window.setupVisibilityHandler = function(dotNetRef) {
    visibilityDotNetRef = dotNetRef;
    
    document.addEventListener('visibilitychange', function() {
        if (visibilityDotNetRef) {
            const isVisible = document.visibilityState === 'visible';
            visibilityDotNetRef.invokeMethodAsync('OnPageVisibilityChanged', isVisible);
        }
    });
    
    // Also handle page focus/blur for additional reliability
    window.addEventListener('focus', function() {
        if (visibilityDotNetRef) {
            visibilityDotNetRef.invokeMethodAsync('OnPageVisibilityChanged', true);
        }
    });
    
    window.addEventListener('blur', function() {
        // Don't report hidden on blur - only use visibilitychange for that
        // This prevents false disconnects when clicking outside the window
    });
    
    console.log('Visibility handler registered');
};

window.thuvu = {
    // Scroll element to bottom
    scrollToBottom: function (element) {
        if (element) {
            element.scrollTop = element.scrollHeight;
        }
    },

    // Scroll to anchor by ID - scrolls the nearest scrollable parent
    scrollToAnchor: function (anchorId) {
        const element = document.getElementById(anchorId);
        if (element) {
            // Find the scrollable parent (tab-content)
            let parent = element.parentElement;
            while (parent) {
                const style = window.getComputedStyle(parent);
                if (style.overflowY === 'auto' || style.overflowY === 'scroll') {
                    parent.scrollTop = parent.scrollHeight;
                    return;
                }
                parent = parent.parentElement;
            }
            // Fallback to scrollIntoView
            element.scrollIntoView({ behavior: 'instant', block: 'end' });
        }
    },

    // Scroll element into view
    scrollIntoView: function (element) {
        if (element) {
            element.scrollIntoView({ behavior: 'smooth', block: 'end' });
        }
    },

    // Copy text to clipboard
    copyToClipboard: async function (text) {
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch (err) {
            console.error('Failed to copy:', err);
            return false;
        }
    },

    // Focus element
    focus: function (element) {
        if (element) {
            element.focus();
        }
    },

    // Get element scroll info
    getScrollInfo: function (element) {
        if (!element) return null;
        return {
            scrollTop: element.scrollTop,
            scrollHeight: element.scrollHeight,
            clientHeight: element.clientHeight,
            isAtBottom: element.scrollTop + element.clientHeight >= element.scrollHeight - 10
        };
    },

    // Register keyboard shortcuts
    registerShortcuts: function (dotNetRef) {
        document.addEventListener('keydown', function (e) {
            // Ctrl+Enter to send
            if (e.ctrlKey && e.key === 'Enter') {
                dotNetRef.invokeMethodAsync('OnCtrlEnter');
                e.preventDefault();
            }
            // Escape to cancel
            if (e.key === 'Escape') {
                dotNetRef.invokeMethodAsync('OnEscape');
            }
            // Ctrl+L to clear
            if (e.ctrlKey && e.key === 'l') {
                dotNetRef.invokeMethodAsync('OnCtrlL');
                e.preventDefault();
            }
        });
    },

    // Syntax highlighting for code blocks (basic)
    highlightCode: function (element) {
        if (!element) return;
        
        const codeBlocks = element.querySelectorAll('pre code, .code-block');
        codeBlocks.forEach(block => {
            // Basic syntax highlighting
            let html = block.innerHTML;
            
            // Keywords
            const keywords = ['function', 'const', 'let', 'var', 'if', 'else', 'for', 'while', 
                            'return', 'class', 'public', 'private', 'static', 'async', 'await',
                            'import', 'export', 'from', 'new', 'this', 'true', 'false', 'null'];
            keywords.forEach(kw => {
                const regex = new RegExp(`\\b(${kw})\\b`, 'g');
                html = html.replace(regex, '<span class="keyword">$1</span>');
            });
            
            // Strings
            html = html.replace(/(["'`])(?:(?!\1)[^\\]|\\.)*\1/g, '<span class="string">$&</span>');
            
            // Comments
            html = html.replace(/(\/\/.*$)/gm, '<span class="comment">$1</span>');
            html = html.replace(/(\/\*[\s\S]*?\*\/)/g, '<span class="comment">$1</span>');
            
            // Numbers
            html = html.replace(/\b(\d+\.?\d*)\b/g, '<span class="number">$1</span>');
            
            block.innerHTML = html;
        });
    },

    // Auto-resize textarea
    autoResize: function (textarea) {
        if (!textarea) return;
        textarea.style.height = 'auto';
        textarea.style.height = Math.min(textarea.scrollHeight, 200) + 'px';
    },

    // Download content as file
    downloadFile: function (filename, content, mimeType) {
        const blob = new Blob([content], { type: mimeType || 'text/plain' });
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = filename;
        a.click();
        URL.revokeObjectURL(url);
    },

    // Local storage helpers
    storage: {
        get: function (key) {
            return localStorage.getItem('thuvu_' + key);
        },
        set: function (key, value) {
            localStorage.setItem('thuvu_' + key, value);
        },
        remove: function (key) {
            localStorage.removeItem('thuvu_' + key);
        }
    },

    // Theme toggle
    setTheme: function (theme) {
        document.documentElement.setAttribute('data-theme', theme);
        this.storage.set('theme', theme);
    },

    // Initialize
    init: function () {
        // Load saved theme
        const savedTheme = this.storage.get('theme') || 'dark';
        this.setTheme(savedTheme);
        
        console.log('T.H.U.V.U. Web Interface initialized');
    }
};

// Initialize on load
document.addEventListener('DOMContentLoaded', function () {
    window.thuvu.init();
});
