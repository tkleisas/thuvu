import type { DotnetNewArgs, DotnetNewResult } from '../../types/tools.d.ts';

/**
 * Create a new .NET project from a template
 * @param template - Template name (console, classlib, webapi, etc.)
 * @param name - Project name
 * @param output - Output directory
 * @returns Result with stdout/stderr and exit code
 */
export async function newProject(
  template: string,
  name?: string,
  output?: string
): Promise<DotnetNewResult>;
export async function newProject(args: DotnetNewArgs): Promise<DotnetNewResult>;
export async function newProject(
  templateOrArgs: string | DotnetNewArgs,
  name?: string,
  output?: string
): Promise<DotnetNewResult> {
  const args: DotnetNewArgs = typeof templateOrArgs === 'string'
    ? { template: templateOrArgs, name, output }
    : templateOrArgs;

  return await __thuvu_bridge__.call<DotnetNewResult>('dotnet_new', args);
}
