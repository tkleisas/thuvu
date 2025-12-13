/**
 * Git tools for version control operations
 */

export { status } from './status.ts';
export { diff, diffStaged, diffUnstaged } from './diff.ts';
export { commit } from './commit.ts';

// Re-export types
export type {
  GitStatusResult,
  GitDiffResult,
  GitCommitResult,
  GitStatusArgs,
  GitDiffArgs,
  GitCommitArgs,
} from '../../types/tools.d.ts';
