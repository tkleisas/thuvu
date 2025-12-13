import type { RunProcessArgs, RunProcessResult } from '../../types/tools.d.ts';

/**
 * Run a whitelisted process
 * @param cmd - Command to run (must be in whitelist: dotnet, git, bash, powershell, etc.)
 * @param args - Command arguments
 * @param cwd - Working directory
 * @param timeoutMs - Timeout in milliseconds
 * @returns Process result with stdout/stderr and exit code
 */
export async function run(
  cmd: string,
  args?: string[],
  cwd?: string,
  timeoutMs?: number
): Promise<RunProcessResult>;
export async function run(processArgs: RunProcessArgs): Promise<RunProcessResult>;
export async function run(
  cmdOrArgs: string | RunProcessArgs,
  args?: string[],
  cwd?: string,
  timeoutMs?: number
): Promise<RunProcessResult> {
  const processArgs: RunProcessArgs = typeof cmdOrArgs === 'string'
    ? { cmd: cmdOrArgs, args, cwd, timeout_ms: timeoutMs }
    : cmdOrArgs;

  return await __thuvu_bridge__.call<RunProcessResult>('run_process', processArgs);
}

/**
 * Run a git command
 */
export async function git(args: string[], cwd?: string): Promise<RunProcessResult> {
  return await run('git', args, cwd);
}

/**
 * Run a dotnet command
 */
export async function dotnet(args: string[], cwd?: string): Promise<RunProcessResult> {
  return await run('dotnet', args, cwd);
}
