import type { RagIndexArgs, RagIndexResult, RagClearArgs, RagClearResult, RagStatsResult } from '../../types/tools.d.ts';

/**
 * Index files for semantic search
 * @param path - Path to file or directory to index
 * @param recursive - Whether to recursively index directories
 * @param pattern - Glob pattern to filter files
 * @returns Index result with file and chunk counts
 */
export async function index(
  path: string,
  recursive?: boolean,
  pattern?: string
): Promise<RagIndexResult>;
export async function index(args: RagIndexArgs): Promise<RagIndexResult>;
export async function index(
  pathOrArgs: string | RagIndexArgs,
  recursive?: boolean,
  pattern?: string
): Promise<RagIndexResult> {
  const args: RagIndexArgs = typeof pathOrArgs === 'string'
    ? { path: pathOrArgs, recursive, pattern }
    : pathOrArgs;

  return await __thuvu_bridge__.call<RagIndexResult>('rag_index', args);
}

/**
 * Clear indexed content
 * @param sourcePath - Optional source path to clear (clears all if not specified)
 * @returns Clear result with deleted chunk count
 */
export async function clear(sourcePath?: string): Promise<RagClearResult>;
export async function clear(args: RagClearArgs): Promise<RagClearResult>;
export async function clear(sourcePathOrArgs?: string | RagClearArgs): Promise<RagClearResult> {
  const args: RagClearArgs = typeof sourcePathOrArgs === 'string' || sourcePathOrArgs === undefined
    ? { source_path: sourcePathOrArgs }
    : sourcePathOrArgs;

  return await __thuvu_bridge__.call<RagClearResult>('rag_clear', args);
}

/**
 * Get RAG index statistics
 * @returns Stats including total chunks, sources, and characters
 */
export async function stats(): Promise<RagStatsResult> {
  return await __thuvu_bridge__.call<RagStatsResult>('rag_stats', {});
}

// Re-export search from separate file
export { search } from './search.ts';

// Re-export types
export type {
  RagSearchResult,
  RagIndexResult,
  RagClearResult,
  RagStatsResult,
  RagSearchArgs,
  RagIndexArgs,
  RagClearArgs,
} from '../../types/tools.d.ts';
