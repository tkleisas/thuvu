import type { SearchFilesArgs, SearchFilesResult } from '../../types/tools.d.ts';

/**
 * Search for files matching a glob pattern and/or containing specific text
 * @param glob - Glob pattern to match files (e.g., "**\/*.cs")
 * @param query - Optional text to search for within files
 * @returns Array of matching file paths
 */
export async function searchFiles(glob?: string, query?: string): Promise<string[]>;
export async function searchFiles(args: SearchFilesArgs): Promise<string[]>;
export async function searchFiles(
  globOrArgs?: string | SearchFilesArgs,
  query?: string
): Promise<string[]> {
  const args: SearchFilesArgs = typeof globOrArgs === 'string' || globOrArgs === undefined
    ? { glob: globOrArgs, query }
    : globOrArgs;

  const result = await __thuvu_bridge__.call<SearchFilesResult>('search_files', args);
  return result.matches;
}
