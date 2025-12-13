import type { ApplyPatchArgs, ApplyPatchResult } from '../../types/tools.d.ts';

/**
 * Apply a unified diff patch to files
 * @param patch - Unified diff patch content
 * @returns Result indicating success and files modified
 */
export async function applyPatch(patch: string): Promise<ApplyPatchResult>;
export async function applyPatch(args: ApplyPatchArgs): Promise<ApplyPatchResult>;
export async function applyPatch(patchOrArgs: string | ApplyPatchArgs): Promise<ApplyPatchResult> {
  const args: ApplyPatchArgs = typeof patchOrArgs === 'string'
    ? { patch: patchOrArgs }
    : patchOrArgs;

  return await __thuvu_bridge__.call<ApplyPatchResult>('apply_patch', args);
}
