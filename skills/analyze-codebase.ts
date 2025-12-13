/**
 * Skill: Analyze Codebase
 * Analyzes project structure, file types, and basic statistics
 */

import { searchFiles, readFile } from '../mcp/servers/filesystem';

export const metadata = {
  name: 'analyze-codebase',
  description: 'Analyze project structure, file types, and code statistics',
  version: '1.0.0',
  parameters: {
    depth: { type: 'number', description: 'Directory depth to analyze', default: 3 },
    includeContent: { type: 'boolean', description: 'Include file content analysis', default: false }
  }
};

export interface AnalyzeParams {
  depth?: number;
  includeContent?: boolean;
}

export interface AnalyzeResult {
  totalFiles: number;
  filesByExtension: Record<string, number>;
  directories: string[];
  largestFiles: Array<{ path: string; lines: number }>;
  totalLines: number;
}

export async function execute(params: AnalyzeParams = {}): Promise<AnalyzeResult> {
  const { depth = 3, includeContent = false } = params;

  // Find all files
  const allFiles = await searchFiles('**/*');
  
  // Group by extension
  const filesByExtension: Record<string, number> = {};
  const directories = new Set<string>();

  for (const file of allFiles) {
    // Get extension
    const ext = file.includes('.') ? file.split('.').pop()! : 'no-ext';
    filesByExtension[ext] = (filesByExtension[ext] || 0) + 1;

    // Get directory
    const dir = file.split(/[/\\]/).slice(0, -1).join('/');
    if (dir) directories.add(dir);
  }

  let totalLines = 0;
  const largestFiles: Array<{ path: string; lines: number }> = [];

  // Analyze content for code files if requested
  if (includeContent) {
    const codeExtensions = ['cs', 'ts', 'js', 'py', 'java', 'cpp', 'c', 'h'];
    const codeFiles = allFiles.filter(f => {
      const ext = f.split('.').pop()?.toLowerCase();
      return ext && codeExtensions.includes(ext);
    }).slice(0, 50); // Limit to 50 files

    for (const file of codeFiles) {
      try {
        const content = await readFile(file);
        const lines = content.content.split('\n').length;
        totalLines += lines;
        largestFiles.push({ path: file, lines });
      } catch {
        // Skip files that can't be read
      }
    }

    // Sort by lines descending
    largestFiles.sort((a, b) => b.lines - a.lines);
  }

  return {
    totalFiles: allFiles.length,
    filesByExtension,
    directories: Array.from(directories).slice(0, 20),
    largestFiles: largestFiles.slice(0, 10),
    totalLines
  };
}
