/**
 * Type definitions for THUVU MCP tool wrappers
 */

// Bridge interface for calling back to C#
declare global {
  const __thuvu_bridge__: {
    call<T = unknown>(toolName: string, args: Record<string, unknown>): Promise<T>;
  };
}

// ============================================================================
// Filesystem Tools
// ============================================================================

export interface ReadFileResult {
  content: string;
  sha256: string;
  encoding: string;
}

export interface WriteFileResult {
  success: boolean;
  sha256: string;
  bytes_written: number;
}

export interface SearchFilesResult {
  matches: string[];
}

export interface ApplyPatchResult {
  success: boolean;
  files_modified: string[];
  error?: string;
}

// ============================================================================
// Git Tools
// ============================================================================

export interface GitStatusResult {
  stdout: string;
  stderr: string;
  exit_code: number;
}

export interface GitDiffResult {
  stdout: string;
  stderr: string;
  exit_code: number;
}

export interface GitCommitResult {
  stdout: string;
  stderr: string;
  exit_code: number;
}

// ============================================================================
// Dotnet Tools
// ============================================================================

export interface DotnetBuildResult {
  stdout: string;
  stderr: string;
  exit_code: number;
  success: boolean;
}

export interface DotnetTestResult {
  stdout: string;
  stderr: string;
  exit_code: number;
  passed: number;
  failed: number;
  skipped: number;
}

export interface DotnetNewResult {
  stdout: string;
  stderr: string;
  exit_code: number;
}

// ============================================================================
// RAG Tools
// ============================================================================

export interface RagSearchResult {
  results: Array<{
    content: string;
    source: string;
    similarity: number;
    metadata?: Record<string, unknown>;
  }>;
  count: number;
}

export interface RagIndexResult {
  success: boolean;
  indexed_files: number;
  indexed_chunks: number;
  error?: string;
}

export interface RagStatsResult {
  enabled: boolean;
  total_chunks: number;
  total_sources: number;
  total_characters: number;
}

export interface RagClearResult {
  success: boolean;
  deleted_chunks: number;
  scope: string;
}

// ============================================================================
// Process Tools
// ============================================================================

export interface RunProcessResult {
  stdout: string;
  stderr: string;
  exit_code: number;
}

// ============================================================================
// Tool Input Types
// ============================================================================

export interface ReadFileArgs {
  path: string;
}

export interface WriteFileArgs {
  path: string;
  content: string;
  expected_sha256?: string;
}

export interface SearchFilesArgs {
  glob?: string;
  query?: string;
}

export interface ApplyPatchArgs {
  patch: string;
}

export interface GitStatusArgs {
  paths?: string[];
  root?: string;
}

export interface GitDiffArgs {
  paths?: string[];
  staged?: boolean;
  context?: number;
  root?: string;
}

export interface GitCommitArgs {
  message: string;
  paths?: string[];
  root?: string;
}

export interface DotnetBuildArgs {
  solution_or_project?: string;
  configuration?: string;
}

export interface DotnetTestArgs {
  solution_or_project?: string;
  filter?: string;
  logger?: string;
}

export interface DotnetNewArgs {
  template: string;
  name?: string;
  output?: string;
}

export interface RagSearchArgs {
  query: string;
  top_k?: number;
}

export interface RagIndexArgs {
  path: string;
  recursive?: boolean;
  pattern?: string;
}

export interface RagClearArgs {
  source_path?: string;
}

export interface RunProcessArgs {
  cmd: string;
  args?: string[];
  cwd?: string;
  timeout_ms?: number;
}

export {};
