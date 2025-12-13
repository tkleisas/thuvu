import type { DotnetBuildArgs, DotnetBuildResult } from '../../types/tools.d.ts';

/**
 * Build a .NET solution or project
 * @param solutionOrProject - Optional path to solution or project file
 * @param configuration - Build configuration (Debug, Release)
 * @returns Build result with stdout/stderr and exit code
 */
export async function build(
  solutionOrProject?: string,
  configuration?: string
): Promise<DotnetBuildResult>;
export async function build(args: DotnetBuildArgs): Promise<DotnetBuildResult>;
export async function build(
  solutionOrProjectOrArgs?: string | DotnetBuildArgs,
  configuration?: string
): Promise<DotnetBuildResult> {
  const args: DotnetBuildArgs = typeof solutionOrProjectOrArgs === 'string' || solutionOrProjectOrArgs === undefined
    ? { solution_or_project: solutionOrProjectOrArgs, configuration }
    : solutionOrProjectOrArgs;

  return await __thuvu_bridge__.call<DotnetBuildResult>('dotnet_build', args);
}
