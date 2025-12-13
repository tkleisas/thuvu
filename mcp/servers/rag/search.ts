import type { RagSearchArgs, RagSearchResult } from '../../types/tools.d.ts';

/**
 * Search indexed content using semantic similarity
 * @param query - Search query
 * @param topK - Number of results to return
 * @returns Search results with similarity scores
 */
export async function search(query: string, topK?: number): Promise<RagSearchResult>;
export async function search(args: RagSearchArgs): Promise<RagSearchResult>;
export async function search(
  queryOrArgs: string | RagSearchArgs,
  topK?: number
): Promise<RagSearchResult> {
  const args: RagSearchArgs = typeof queryOrArgs === 'string'
    ? { query: queryOrArgs, top_k: topK }
    : queryOrArgs;

  return await __thuvu_bridge__.call<RagSearchResult>('rag_search', args);
}
