/**
 * Bridge for communication between TypeScript sandbox and C# host
 * Uses JSON-RPC 2.0 over stdin/stdout
 * 
 * IMPORTANT: Uses TextLineStream for proper line-by-line stdin reading
 */

interface JsonRpcRequest {
  jsonrpc: '2.0';
  id: number;
  method: string;
  params: Record<string, unknown>;
}

interface JsonRpcResponse {
  jsonrpc: '2.0';
  id: number;
  result?: unknown;
  error?: {
    code: number;
    message: string;
    data?: unknown;
  };
}

interface ExecutionRequest {
  code: string;
  timeout?: number;
}

// Use a simple line reader that doesn't conflict
async function* readLines(): AsyncGenerator<string> {
  const decoder = new TextDecoder();
  let buffer = '';
  
  // Read stdin in a way that doesn't lock the readable stream
  const reader = Deno.stdin.readable.getReader();
  
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      
      buffer += decoder.decode(value, { stream: true });
      
      let newlineIndex: number;
      while ((newlineIndex = buffer.indexOf('\n')) !== -1) {
        const line = buffer.slice(0, newlineIndex);
        buffer = buffer.slice(newlineIndex + 1);
        yield line;
      }
    }
    
    // Yield any remaining content
    if (buffer) {
      yield buffer;
    }
  } finally {
    reader.releaseLock();
  }
}

class ThuvuBridge {
  private requestId = 0;
  private pendingRequests = new Map<number, {
    resolve: (value: unknown) => void;
    reject: (error: Error) => void;
  }>();
  private encoder = new TextEncoder();
  private lineReader: AsyncGenerator<string> | null = null;
  private executeResolve: ((req: ExecutionRequest) => void) | null = null;
  private initialized = false;

  constructor() {}

  /**
   * Initialize and wait for the EXECUTE command
   */
  async waitForExecuteCommand(): Promise<ExecutionRequest | null> {
    if (!this.lineReader) {
      this.lineReader = readLines();
    }
    
    // Read the first line which should be the EXECUTE command
    const result = await this.lineReader.next();
    if (result.done) return null;
    
    const line = result.value.trim();
    if (line.startsWith('EXECUTE:')) {
      const requestJson = line.slice('EXECUTE:'.length);
      try {
        const request: ExecutionRequest = JSON.parse(requestJson);
        // Start background reading for responses after we got the EXECUTE
        this.startBackgroundReading();
        return request;
      } catch (e) {
        console.error('Failed to parse EXECUTE request:', e);
        return null;
      }
    }
    
    console.error('Expected EXECUTE command, got:', line);
    return null;
  }
  
  /**
   * Start reading responses in the background
   */
  private async startBackgroundReading(): Promise<void> {
    if (!this.lineReader) return;
    
    for await (const line of this.lineReader) {
      const trimmed = line.trim();
      if (!trimmed) continue;
      
      // Try to parse as JSON-RPC response
      if (trimmed.startsWith('{')) {
        try {
          const response: JsonRpcResponse = JSON.parse(trimmed);
          this.handleResponse(response);
        } catch (e) {
          // Not valid JSON, ignore
        }
      }
    }
  }

  /**
   * Handle a JSON-RPC response from C#
   */
  handleResponse(response: JsonRpcResponse): void {
    const pending = this.pendingRequests.get(response.id);
    if (!pending) {
      return;
    }

    this.pendingRequests.delete(response.id);

    if (response.error) {
      pending.reject(new Error(response.error.message));
    } else {
      pending.resolve(response.result);
    }
  }

  async call<T = unknown>(toolName: string, args: Record<string, unknown>): Promise<T> {
    const id = ++this.requestId;
    
    const request: JsonRpcRequest = {
      jsonrpc: '2.0',
      id,
      method: toolName,
      params: args,
    };

    const promise = new Promise<T>((resolve, reject) => {
      this.pendingRequests.set(id, {
        resolve: resolve as (value: unknown) => void,
        reject,
      });
    });

    // Write request to stdout
    const line = JSON.stringify(request) + '\n';
    await Deno.stdout.write(this.encoder.encode(line));

    return promise;
  }
}

// Create global bridge instance
const bridge = new ThuvuBridge();

// Expose bridge as global for tool modules
(globalThis as Record<string, unknown>).__thuvu_bridge__ = bridge;

export { bridge };
