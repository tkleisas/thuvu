/**
 * Bridge for communication between TypeScript sandbox and C# host
 * Uses JSON-RPC 2.0 over stdin/stdout
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

class ThuvuBridge {
  private requestId = 0;
  private pendingRequests = new Map<number, {
    resolve: (value: unknown) => void;
    reject: (error: Error) => void;
  }>();
  private decoder = new TextDecoder();
  private encoder = new TextEncoder();
  private buffer = '';

  constructor() {
    this.startReading();
  }

  private async startReading(): Promise<void> {
    const reader = Deno.stdin.readable.getReader();
    
    try {
      while (true) {
        const { done, value } = await reader.read();
        if (done) break;
        
        this.buffer += this.decoder.decode(value, { stream: true });
        this.processBuffer();
      }
    } catch (error) {
      console.error('Bridge read error:', error);
    }
  }

  private processBuffer(): void {
    // Process line-delimited JSON
    let newlineIndex: number;
    while ((newlineIndex = this.buffer.indexOf('\n')) !== -1) {
      const line = this.buffer.slice(0, newlineIndex).trim();
      this.buffer = this.buffer.slice(newlineIndex + 1);
      
      if (line) {
        try {
          const response: JsonRpcResponse = JSON.parse(line);
          this.handleResponse(response);
        } catch (e) {
          console.error('Failed to parse response:', line, e);
        }
      }
    }
  }

  private handleResponse(response: JsonRpcResponse): void {
    const pending = this.pendingRequests.get(response.id);
    if (!pending) {
      console.error('No pending request for id:', response.id);
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

// Expose as global
(globalThis as Record<string, unknown>).__thuvu_bridge__ = bridge;

export { bridge };
