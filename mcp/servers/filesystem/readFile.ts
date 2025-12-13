import type { ReadFileArgs, ReadFileResult } from '../../types/tools.d.ts';

/**
 * Read the contents of a file
 * @param path - Path to the file to read
 * @returns File content, SHA256 hash, and encoding
 */
export async function readFile(path: string): Promise<ReadFileResult>;
export async function readFile(args: ReadFileArgs): Promise<ReadFileResult>;
export async function readFile(pathOrArgs: string | ReadFileArgs): Promise<ReadFileResult> {
  const args: ReadFileArgs = typeof pathOrArgs === 'string' 
    ? { path: pathOrArgs } 
    : pathOrArgs;
  
  return await __thuvu_bridge__.call<ReadFileResult>('read_file', args);
}
