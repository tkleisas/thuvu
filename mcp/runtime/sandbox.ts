/**
 * Sandbox bootstrap script for executing agent-generated TypeScript code
 * This is the entry point that Deno runs
 */

import { bridge } from './bridge.ts';

interface ExecutionRequest {
  code: string;
  timeout?: number;
}

interface ExecutionResult {
  success: boolean;
  result?: unknown;
  output?: string;  // Captured console.log output
  error?: string;
  duration: number;
}

/**
 * Execute agent-generated code in a sandboxed context
 */
async function executeCode(code: string): Promise<unknown> {
  // Get the mcp directory URL for imports - keep it as a proper file:// URL
  const runtimeUrl = new URL('.', import.meta.url);
  const mcpUrl = new URL('..', runtimeUrl);
  const fsUrl = new URL('servers/filesystem/index.ts', mcpUrl).href;
  const gitUrl = new URL('servers/git/index.ts', mcpUrl).href;
  const processUrl = new URL('servers/process/index.ts', mcpUrl).href;
  
  // Wrap the code so it exports its result as default
  // Auto-inject tool imports so users don't need to import them
  const wrappedCode = `
    import { searchFiles, readFile, writeFile, applyPatch } from '${fsUrl}';
    import { status, diff, diffStaged, diffUnstaged, commit } from '${gitUrl}';
    import { run as runCommand, git, dotnet } from '${processUrl}';
    
    const __result__ = await (async () => {
      ${code}
    })();
    export default __result__;
  `;

  // Create a dynamic import URL for the code
  const blob = new Blob([wrappedCode], { type: 'application/typescript' });
  const url = URL.createObjectURL(blob);

  try {
    const module = await import(url);
    return module.default;
  } finally {
    URL.revokeObjectURL(url);
  }
}

/**
 * Main entry point - reads execution request from stdin
 */
async function main(): Promise<void> {
  // Wait for the EXECUTE command
  const request = await bridge.waitForExecuteCommand();
  
  if (!request) {
    console.error('No EXECUTE command received');
    Deno.exit(1);
    return;
  }

  const startTime = performance.now();
  let result: ExecutionResult;
  
  // Capture console output by overriding console.log
  const capturedOutput: string[] = [];
  const originalLog = console.log;
  console.log = (...args: unknown[]) => {
    const line = args.map(a => typeof a === 'string' ? a : JSON.stringify(a)).join(' ');
    capturedOutput.push(line);
    // Also write to stderr so it appears in debug output
    console.error('[CODE OUTPUT]', line);
  };

  try {
    const output = await executeCode(request.code);
    result = {
      success: true,
      result: output,
      output: capturedOutput.length > 0 ? capturedOutput.join('\n') : undefined,
      duration: performance.now() - startTime,
    };
  } catch (error) {
    result = {
      success: false,
      error: error instanceof Error ? error.message : String(error),
      output: capturedOutput.length > 0 ? capturedOutput.join('\n') : undefined,
      duration: performance.now() - startTime,
    };
  } finally {
    // Restore original console.log
    console.log = originalLog;
  }

  // Write result to stdout
  const encoder = new TextEncoder();
  const resultLine = 'RESULT:' + JSON.stringify(result) + '\n';
  await Deno.stdout.write(encoder.encode(resultLine));
  
  // Exit after execution
  Deno.exit(result.success ? 0 : 1);
}

// Run if this is the main module
if (import.meta.main) {
  main().catch(console.error);
}
