import type { GitDiffArgs, GitDiffResult } from '../../types/tools.d.ts';

/**
 * Get git diff for the repository
 * @param args - Diff options (paths, staged, context, root)
 * @returns Git diff output
 */
export async function diff(args?: GitDiffArgs): Promise<GitDiffResult> {
  return await __thuvu_bridge__.call<GitDiffResult>('git_diff', args ?? {});
}

/**
 * Get staged changes diff
 */
export async function diffStaged(paths?: string[], context?: number): Promise<GitDiffResult> {
  return await diff({ paths, staged: true, context });
}

/**
 * Get unstaged changes diff
 */
export async function diffUnstaged(paths?: string[], context?: number): Promise<GitDiffResult> {
  return await diff({ paths, staged: false, context });
}
