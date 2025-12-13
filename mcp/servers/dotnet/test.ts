import type { DotnetTestArgs, DotnetTestResult } from '../../types/tools.d.ts';

/**
 * Run .NET tests
 * @param solutionOrProject - Optional path to solution or project file
 * @param filter - Optional test filter expression
 * @param logger - Logger type (trx, console)
 * @returns Test result with pass/fail counts
 */
export async function test(
  solutionOrProject?: string,
  filter?: string,
  logger?: string
): Promise<DotnetTestResult>;
export async function test(args: DotnetTestArgs): Promise<DotnetTestResult>;
export async function test(
  solutionOrProjectOrArgs?: string | DotnetTestArgs,
  filter?: string,
  logger?: string
): Promise<DotnetTestResult> {
  const args: DotnetTestArgs = typeof solutionOrProjectOrArgs === 'string' || solutionOrProjectOrArgs === undefined
    ? { solution_or_project: solutionOrProjectOrArgs, filter, logger }
    : solutionOrProjectOrArgs;

  return await __thuvu_bridge__.call<DotnetTestResult>('dotnet_test', args);
}
