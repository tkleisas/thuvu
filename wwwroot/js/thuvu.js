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

    // Read image from clipboard event
    readClipboardImage: async function (dotNetRef, clipboardData) {
        if (!clipboardData || !clipboardData.items) {
            return null;
        }
        
        for (let i = 0; i < clipboardData.items.length; i++) {
            const item = clipboardData.items[i];
            if (item.type.startsWith('image/')) {
                const blob = item.getAsFile();
                if (blob) {
                    return await this.readImageBlob(blob);
                }
            }
        }
        return null;
    },
    
    // Read image from file input or drop
    readImageFile: async function (file) {
        if (!file || !file.type.startsWith('image/')) {
            return null;
        }
        return await this.readImageBlob(file);
    },
    
    // Resize image to max dimensions while maintaining aspect ratio
    resizeImage: function (blob, maxWidth = 1024, maxHeight = 1024) {
        return new Promise((resolve) => {
            const img = new Image();
            img.onload = function() {
                let width = img.width;
                let height = img.height;
                
                // Calculate new dimensions maintaining aspect ratio
                if (width > maxWidth || height > maxHeight) {
                    const ratio = Math.min(maxWidth / width, maxHeight / height);
                    width = Math.round(width * ratio);
                    height = Math.round(height * ratio);
                }
                
                // Create canvas and draw resized image
                const canvas = document.createElement('canvas');
                canvas.width = width;
                canvas.height = height;
                const ctx = canvas.getContext('2d');
                ctx.drawImage(img, 0, 0, width, height);
                
                // Convert to blob with quality setting
                canvas.toBlob((resizedBlob) => {
                    resolve(resizedBlob);
                }, 'image/jpeg', 0.85);
            };
            img.onerror = function() {
                resolve(blob); // Return original on error
            };
            img.src = URL.createObjectURL(blob);
        });
    },
    
    // Read blob as base64 (with optional resize)
    readImageBlob: async function (blob, resize = true) {
        // Resize large images to prevent vision model errors
        if (resize && blob.size > 500000) { // > 500KB
            console.log(`Resizing large image: ${(blob.size / 1024).toFixed(1)}KB`);
            blob = await this.resizeImage(blob);
            console.log(`Resized to: ${(blob.size / 1024).toFixed(1)}KB`);
        }
        
        return new Promise((resolve) => {
            const reader = new FileReader();
            reader.onload = function(e) {
                const dataUrl = e.target.result;
                // Extract base64 and mime type from data URL
                const matches = dataUrl.match(/^data:([^;]+);base64,(.+)$/);
                if (matches) {
                    resolve({
                        mimeType: matches[1],
                        base64: matches[2],
                        name: blob.name || 'pasted-image',
                        size: blob.size
                    });
                } else {
                    resolve(null);
                }
            };
            reader.onerror = function() {
                resolve(null);
            };
            reader.readAsDataURL(blob);
        });
    },
    
    // Handle paste event and extract image
    handlePasteEvent: async function (dotNetRef) {
        try {
            const clipboardItems = await navigator.clipboard.read();
            for (const item of clipboardItems) {
                for (const type of item.types) {
                    if (type.startsWith('image/')) {
                        const blob = await item.getType(type);
                        const result = await this.readImageBlob(blob, true); // resize enabled
                        if (result && dotNetRef) {
                            await dotNetRef.invokeMethodAsync('OnImagePasted', result.base64, result.mimeType, result.name);
                        }
                        return true;
                    }
                }
            }
        } catch (err) {
            console.log('Clipboard API not available, will use event data');
        }
        return false;
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
