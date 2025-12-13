import type { WriteFileArgs, WriteFileResult } from '../../types/tools.d.ts';

/**
 * Write content to a file
 * @param path - Path to the file to write
 * @param content - Content to write
 * @param expectedSha256 - Optional SHA256 of expected current content (for optimistic locking)
 * @returns Write result with new SHA256 hash
 */
export async function writeFile(
  path: string, 
  content: string, 
  expectedSha256?: string
): Promise<WriteFileResult>;
export async function writeFile(args: WriteFileArgs): Promise<WriteFileResult>;
export async function writeFile(
  pathOrArgs: string | WriteFileArgs,
  content?: string,
  expectedSha256?: string
): Promise<WriteFileResult> {
  const args: WriteFileArgs = typeof pathOrArgs === 'string'
    ? { path: pathOrArgs, content: content!, expected_sha256: expectedSha256 }
    : pathOrArgs;

  return await __thuvu_bridge__.call<WriteFileResult>('write_file', args);
}
