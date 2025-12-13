/**
 * Skill: Run Tests and Analyze Failures
 * Runs tests and provides detailed analysis of failures
 */

import { test, build } from '../mcp/servers/dotnet';
import { readFile, searchFiles } from '../mcp/servers/filesystem';
import { diff, status } from '../mcp/servers/git';

export const metadata = {
  name: 'run-tests-and-fix',
  description: 'Run tests, analyze failures, and suggest fixes',
  version: '1.0.0',
  parameters: {
    project: { type: 'string', description: 'Project or solution to test' },
    filter: { type: 'string', description: 'Test filter expression' },
    buildFirst: { type: 'boolean', description: 'Build before testing', default: true }
  }
};

export interface TestParams {
  project?: string;
  filter?: string;
  buildFirst?: boolean;
}

export interface TestAnalysis {
  buildSuccess: boolean;
  buildOutput?: string;
  testsPassed: number;
  testsFailed: number;
  testsSkipped: number;
  failures: Array<{
    name: string;
    error: string;
    stackTrace?: string;
  }>;
  suggestions: string[];
  gitStatus: string;
}

export async function execute(params: TestParams = {}): Promise<TestAnalysis> {
  const { project, filter, buildFirst = true } = params;

  const result: TestAnalysis = {
    buildSuccess: true,
    testsPassed: 0,
    testsFailed: 0,
    testsSkipped: 0,
    failures: [],
    suggestions: [],
    gitStatus: ''
  };

  // Build first if requested
  if (buildFirst) {
    const buildResult = await build(project);
    result.buildSuccess = buildResult.exit_code === 0;
    
    if (!result.buildSuccess) {
      result.buildOutput = buildResult.stderr || buildResult.stdout;
      result.suggestions.push('Fix build errors before running tests');
      return result;
    }
  }

  // Run tests
  const testResult = await test(project, filter);
  
  // Parse test output
  const output = testResult.stdout;
  
  // Extract counts from output
  const passedMatch = output.match(/Passed:\s*(\d+)/);
  const failedMatch = output.match(/Failed:\s*(\d+)/);
  const skippedMatch = output.match(/Skipped:\s*(\d+)/);
  
  result.testsPassed = passedMatch ? parseInt(passedMatch[1]) : 0;
  result.testsFailed = failedMatch ? parseInt(failedMatch[1]) : 0;
  result.testsSkipped = skippedMatch ? parseInt(skippedMatch[1]) : 0;

  // If tests failed, analyze
  if (result.testsFailed > 0) {
    // Extract failure details (simplified parsing)
    const failureBlocks = output.split(/Failed\s+/g).slice(1);
    
    for (const block of failureBlocks.slice(0, 5)) {
      const lines = block.split('\n');
      const name = lines[0]?.trim() || 'Unknown';
      const error = lines.slice(1).join('\n').trim();
      
      result.failures.push({
        name,
        error: error.slice(0, 500)
      });
    }

    // Generate suggestions
    if (output.includes('NullReferenceException')) {
      result.suggestions.push('Check for null values before accessing object properties');
    }
    if (output.includes('Assert.')) {
      result.suggestions.push('Review assertion values - expected vs actual may be swapped');
    }
    if (output.includes('timeout')) {
      result.suggestions.push('Consider increasing test timeout or checking for async issues');
    }
  }

  // Get git status for context
  const statusResult = await status();
  result.gitStatus = statusResult.stdout;

  return result;
}
