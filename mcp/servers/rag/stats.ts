import type { RagStatsResult } from '../../types/tools.d.ts';

/**
 * Get RAG index statistics
 * @returns Stats including total chunks, sources, and characters
 */
export async function stats(): Promise<RagStatsResult> {
  return await __thuvu_bridge__.call<RagStatsResult>('rag_stats', {});
}
