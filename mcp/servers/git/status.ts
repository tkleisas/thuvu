import type { GitStatusArgs, GitStatusResult } from '../../types/tools.d.ts';

/**
 * Get git status for the repository
 * @param paths - Optional paths to check status for
 * @param root - Optional repository root path
 * @returns Git status output
 */
export async function status(paths?: string[], root?: string): Promise<GitStatusResult>;
export async function status(args: GitStatusArgs): Promise<GitStatusResult>;
export async function status(
  pathsOrArgs?: string[] | GitStatusArgs,
  root?: string
): Promise<GitStatusResult> {
  const args: GitStatusArgs = Array.isArray(pathsOrArgs) || pathsOrArgs === undefined
    ? { paths: pathsOrArgs, root }
    : pathsOrArgs;

  return await __thuvu_bridge__.call<GitStatusResult>('git_status', args);
}
