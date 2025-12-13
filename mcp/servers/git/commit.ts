import type { GitCommitArgs, GitCommitResult } from '../../types/tools.d.ts';

/**
 * Create a git commit
 * @param message - Commit message
 * @param paths - Optional paths to commit
 * @param root - Optional repository root path
 * @returns Commit result
 */
export async function commit(
  message: string,
  paths?: string[],
  root?: string
): Promise<GitCommitResult>;
export async function commit(args: GitCommitArgs): Promise<GitCommitResult>;
export async function commit(
  messageOrArgs: string | GitCommitArgs,
  paths?: string[],
  root?: string
): Promise<GitCommitResult> {
  const args: GitCommitArgs = typeof messageOrArgs === 'string'
    ? { message: messageOrArgs, paths, root }
    : messageOrArgs;

  return await __thuvu_bridge__.call<GitCommitResult>('git_commit', args);
}
