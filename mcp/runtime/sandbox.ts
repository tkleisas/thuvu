/**
 * Sandbox bootstrap script for executing agent-generated TypeScript code
 * This is the entry point that Deno runs
 */

import './bridge.ts';

interface ExecutionRequest {
  code: string;
  timeout?: number;
}

interface ExecutionResult {
  success: boolean;
  result?: unknown;
  error?: string;
  duration: number;
}

/**
 * Execute agent-generated code in a sandboxed context
 */
async function executeCode(code: string): Promise<unknown> {
  // Wrap the code in an async function to support top-level await
  const wrappedCode = `
    (async () => {
      ${code}
    })()
  `;

  // Create a dynamic import URL for the code
  const blob = new Blob([wrappedCode], { type: 'application/typescript' });
  const url = URL.createObjectURL(blob);

  try {
    const module = await import(url);
    return module.default ?? module;
  } finally {
    URL.revokeObjectURL(url);
  }
}

/**
 * Main entry point - reads execution request from stdin
 */
async function main(): Promise<void> {
  const decoder = new TextDecoder();
  let buffer = '';

  // Read the execution request from stdin
  for await (const chunk of Deno.stdin.readable) {
    buffer += decoder.decode(chunk, { stream: true });
    
    // Look for the execution request (first line)
    const newlineIndex = buffer.indexOf('\n');
    if (newlineIndex !== -1) {
      const requestLine = buffer.slice(0, newlineIndex).trim();
      buffer = buffer.slice(newlineIndex + 1);

      if (requestLine.startsWith('EXECUTE:')) {
        const requestJson = requestLine.slice('EXECUTE:'.length);
        const request: ExecutionRequest = JSON.parse(requestJson);
        
        const startTime = performance.now();
        let result: ExecutionResult;

        try {
          const output = await executeCode(request.code);
          result = {
            success: true,
            result: output,
            duration: performance.now() - startTime,
          };
        } catch (error) {
          result = {
            success: false,
            error: error instanceof Error ? error.message : String(error),
            duration: performance.now() - startTime,
          };
        }

        // Write result to stdout
        const encoder = new TextEncoder();
        const resultLine = 'RESULT:' + JSON.stringify(result) + '\n';
        await Deno.stdout.write(encoder.encode(resultLine));
        
        // Exit after execution
        Deno.exit(result.success ? 0 : 1);
      }
    }
  }
}

// Run if this is the main module
if (import.meta.main) {
  main().catch(console.error);
}
