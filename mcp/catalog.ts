/**
 * Tool catalog for progressive tool discovery
 * Allows searching and loading tool definitions on-demand
 */

export interface ToolInfo {
  name: string;
  server: string;
  description: string;
  signature: string;
  category: string;
  examples?: string[];
}

export interface ToolSchema {
  name: string;
  description: string;
  parameters: {
    type: string;
    properties: Record<string, {
      type: string;
      description: string;
      required?: boolean;
      default?: unknown;
    }>;
    required?: string[];
  };
  returns: {
    type: string;
    description: string;
    properties?: Record<string, unknown>;
  };
}

/**
 * Complete catalog of all available tools
 */
const TOOL_CATALOG: ToolInfo[] = [
  // Filesystem tools
  {
    name: 'readFile',
    server: 'filesystem',
    description: 'Read the contents of a file and get its SHA256 hash',
    signature: 'readFile(path: string): Promise<{ content: string, sha256: string, encoding: string }>',
    category: 'io',
    examples: ['const file = await readFile("src/main.ts");', 'const { content } = await readFile(path);']
  },
  {
    name: 'writeFile',
    server: 'filesystem',
    description: 'Write content to a file with optional optimistic locking via SHA256',
    signature: 'writeFile(path: string, content: string, expectedSha256?: string): Promise<{ success: boolean, sha256: string, bytes_written: number }>',
    category: 'io',
    examples: ['await writeFile("output.txt", "Hello World");']
  },
  {
    name: 'searchFiles',
    server: 'filesystem',
    description: 'Search for files matching a glob pattern and optionally containing specific text',
    signature: 'searchFiles(glob?: string, query?: string): Promise<string[]>',
    category: 'search',
    examples: ['const csFiles = await searchFiles("**/*.cs");', 'const files = await searchFiles("src/**/*.ts", "import");']
  },
  {
    name: 'applyPatch',
    server: 'filesystem',
    description: 'Apply a unified diff patch to modify files',
    signature: 'applyPatch(patch: string): Promise<{ success: boolean, files_modified: string[], error?: string }>',
    category: 'io',
    examples: ['await applyPatch(unifiedDiff);']
  },

  // Git tools
  {
    name: 'status',
    server: 'git',
    description: 'Get git repository status',
    signature: 'status(paths?: string[], root?: string): Promise<{ stdout: string, stderr: string, exit_code: number }>',
    category: 'vcs',
    examples: ['const { stdout } = await status();']
  },
  {
    name: 'diff',
    server: 'git',
    description: 'Get git diff for staged or unstaged changes',
    signature: 'diff(options?: { paths?: string[], staged?: boolean, context?: number, root?: string }): Promise<{ stdout: string, stderr: string, exit_code: number }>',
    category: 'vcs',
    examples: ['const { stdout } = await diff({ staged: true });', 'const changes = await diff({ paths: ["src/"] });']
  },
  {
    name: 'commit',
    server: 'git',
    description: 'Create a git commit with a message',
    signature: 'commit(message: string, paths?: string[], root?: string): Promise<{ stdout: string, stderr: string, exit_code: number }>',
    category: 'vcs',
    examples: ['await commit("Fix bug in parser");']
  },

  // Dotnet tools
  {
    name: 'build',
    server: 'dotnet',
    description: 'Build a .NET solution or project',
    signature: 'build(solutionOrProject?: string, configuration?: string): Promise<{ stdout: string, stderr: string, exit_code: number, success: boolean }>',
    category: 'build',
    examples: ['await build();', 'await build("MyApp.sln", "Release");']
  },
  {
    name: 'test',
    server: 'dotnet',
    description: 'Run .NET tests and get results',
    signature: 'test(solutionOrProject?: string, filter?: string, logger?: string): Promise<{ stdout: string, stderr: string, exit_code: number, passed: number, failed: number, skipped: number }>',
    category: 'test',
    examples: ['const results = await test();', 'await test(undefined, "FullyQualifiedName~MyTest");']
  },
  {
    name: 'newProject',
    server: 'dotnet',
    description: 'Create a new .NET project from a template',
    signature: 'newProject(template: string, name?: string, output?: string): Promise<{ stdout: string, stderr: string, exit_code: number }>',
    category: 'scaffold',
    examples: ['await newProject("console", "MyApp");', 'await newProject("webapi", "MyApi", "./src");']
  },

  // RAG tools
  {
    name: 'search',
    server: 'rag',
    description: 'Search indexed content using semantic similarity',
    signature: 'search(query: string, topK?: number): Promise<{ results: Array<{ content: string, source: string, similarity: number }>, count: number }>',
    category: 'search',
    examples: ['const results = await search("HTTP request handling");']
  },
  {
    name: 'index',
    server: 'rag',
    description: 'Index files for semantic search',
    signature: 'index(path: string, recursive?: boolean, pattern?: string): Promise<{ success: boolean, indexed_files: number, indexed_chunks: number }>',
    category: 'index',
    examples: ['await index("src/", true, "*.cs");']
  },
  {
    name: 'stats',
    server: 'rag',
    description: 'Get RAG index statistics',
    signature: 'stats(): Promise<{ enabled: boolean, total_chunks: number, total_sources: number, total_characters: number }>',
    category: 'info',
    examples: ['const { total_chunks } = await stats();']
  },
  {
    name: 'clear',
    server: 'rag',
    description: 'Clear indexed content',
    signature: 'clear(sourcePath?: string): Promise<{ success: boolean, deleted_chunks: number, scope: string }>',
    category: 'index',
    examples: ['await clear();', 'await clear("src/old/");']
  },

  // Process tools
  {
    name: 'run',
    server: 'process',
    description: 'Run a whitelisted command (dotnet, git, npm, node)',
    signature: 'run(cmd: string, args?: string[], cwd?: string, timeoutMs?: number): Promise<{ stdout: string, stderr: string, exit_code: number }>',
    category: 'shell',
    examples: ['await run("dotnet", ["build"]);', 'await run("git", ["log", "--oneline", "-5"]);']
  },
  {
    name: 'git',
    server: 'process',
    description: 'Run a git command',
    signature: 'git(args: string[], cwd?: string): Promise<{ stdout: string, stderr: string, exit_code: number }>',
    category: 'shell',
    examples: ['await git(["log", "--oneline", "-10"]);']
  },
  {
    name: 'dotnet',
    server: 'process',
    description: 'Run a dotnet command',
    signature: 'dotnet(args: string[], cwd?: string): Promise<{ stdout: string, stderr: string, exit_code: number }>',
    category: 'shell',
    examples: ['await dotnet(["nuget", "list"]);']
  }
];

/**
 * Tool schemas with full parameter definitions
 */
const TOOL_SCHEMAS: Record<string, ToolSchema> = {
  'filesystem.readFile': {
    name: 'readFile',
    description: 'Read the contents of a file',
    parameters: {
      type: 'object',
      properties: {
        path: { type: 'string', description: 'Path to the file to read', required: true }
      },
      required: ['path']
    },
    returns: {
      type: 'object',
      description: 'File content and metadata',
      properties: {
        content: { type: 'string' },
        sha256: { type: 'string' },
        encoding: { type: 'string' }
      }
    }
  },
  'filesystem.writeFile': {
    name: 'writeFile',
    description: 'Write content to a file',
    parameters: {
      type: 'object',
      properties: {
        path: { type: 'string', description: 'Path to the file to write', required: true },
        content: { type: 'string', description: 'Content to write', required: true },
        expected_sha256: { type: 'string', description: 'Expected SHA256 for optimistic locking' }
      },
      required: ['path', 'content']
    },
    returns: {
      type: 'object',
      description: 'Write result',
      properties: {
        success: { type: 'boolean' },
        sha256: { type: 'string' },
        bytes_written: { type: 'number' }
      }
    }
  },
  'filesystem.searchFiles': {
    name: 'searchFiles',
    description: 'Search for files',
    parameters: {
      type: 'object',
      properties: {
        glob: { type: 'string', description: 'Glob pattern (e.g., **/*.cs)', default: '**/*' },
        query: { type: 'string', description: 'Text to search for in files' }
      }
    },
    returns: {
      type: 'array',
      description: 'Array of matching file paths'
    }
  },
  'git.status': {
    name: 'status',
    description: 'Get git status',
    parameters: {
      type: 'object',
      properties: {
        paths: { type: 'array', description: 'Paths to check' },
        root: { type: 'string', description: 'Repository root' }
      }
    },
    returns: {
      type: 'object',
      description: 'Git status output'
    }
  },
  'git.diff': {
    name: 'diff',
    description: 'Get git diff',
    parameters: {
      type: 'object',
      properties: {
        paths: { type: 'array', description: 'Paths to diff' },
        staged: { type: 'boolean', description: 'Show staged changes' },
        context: { type: 'number', description: 'Lines of context', default: 3 }
      }
    },
    returns: {
      type: 'object',
      description: 'Git diff output'
    }
  },
  'dotnet.build': {
    name: 'build',
    description: 'Build .NET project',
    parameters: {
      type: 'object',
      properties: {
        solution_or_project: { type: 'string', description: 'Path to solution or project' },
        configuration: { type: 'string', description: 'Build configuration', default: 'Debug' }
      }
    },
    returns: {
      type: 'object',
      description: 'Build result'
    }
  },
  'dotnet.test': {
    name: 'test',
    description: 'Run .NET tests',
    parameters: {
      type: 'object',
      properties: {
        solution_or_project: { type: 'string', description: 'Path to solution or project' },
        filter: { type: 'string', description: 'Test filter expression' },
        logger: { type: 'string', description: 'Logger type', default: 'trx' }
      }
    },
    returns: {
      type: 'object',
      description: 'Test results'
    }
  },
  'rag.search': {
    name: 'search',
    description: 'Semantic search',
    parameters: {
      type: 'object',
      properties: {
        query: { type: 'string', description: 'Search query', required: true },
        top_k: { type: 'number', description: 'Number of results', default: 5 }
      },
      required: ['query']
    },
    returns: {
      type: 'object',
      description: 'Search results'
    }
  },
  'process.run': {
    name: 'run',
    description: 'Run a command',
    parameters: {
      type: 'object',
      properties: {
        cmd: { type: 'string', description: 'Command to run', required: true },
        args: { type: 'array', description: 'Command arguments' },
        cwd: { type: 'string', description: 'Working directory' },
        timeout_ms: { type: 'number', description: 'Timeout in ms', default: 120000 }
      },
      required: ['cmd']
    },
    returns: {
      type: 'object',
      description: 'Command output'
    }
  }
};

/**
 * Search for tools matching a query
 * @param query - Search query (matches name, description, category)
 * @returns Matching tools
 */
export function searchTools(query: string): ToolInfo[] {
  const q = query.toLowerCase();
  return TOOL_CATALOG.filter(tool => 
    tool.name.toLowerCase().includes(q) ||
    tool.description.toLowerCase().includes(q) ||
    tool.category.toLowerCase().includes(q) ||
    tool.server.toLowerCase().includes(q)
  );
}

/**
 * Get tools by category
 * @param category - Category to filter by
 */
export function getToolsByCategory(category: string): ToolInfo[] {
  return TOOL_CATALOG.filter(tool => tool.category === category);
}

/**
 * Get tools by server
 * @param server - Server name (filesystem, git, dotnet, rag, process)
 */
export function getToolsByServer(server: string): ToolInfo[] {
  return TOOL_CATALOG.filter(tool => tool.server === server);
}

/**
 * Get full schema for a specific tool
 * @param server - Server name
 * @param toolName - Tool name
 */
export function getToolSchema(server: string, toolName: string): ToolSchema | undefined {
  return TOOL_SCHEMAS[`${server}.${toolName}`];
}

/**
 * Get all available tools (minimal info)
 */
export function listTools(): Array<{ name: string; server: string; description: string }> {
  return TOOL_CATALOG.map(t => ({
    name: t.name,
    server: t.server,
    description: t.description
  }));
}

/**
 * Get all categories
 */
export function getCategories(): string[] {
  return [...new Set(TOOL_CATALOG.map(t => t.category))];
}

/**
 * Get all servers
 */
export function getServers(): string[] {
  return [...new Set(TOOL_CATALOG.map(t => t.server))];
}

/**
 * Generate import statement for tools
 * @param tools - Tools to import
 */
export function generateImports(tools: ToolInfo[]): string {
  const byServer = new Map<string, string[]>();
  for (const tool of tools) {
    const list = byServer.get(tool.server) || [];
    list.push(tool.name);
    byServer.set(tool.server, list);
  }

  const imports: string[] = [];
  for (const [server, names] of byServer) {
    imports.push(`import { ${names.join(', ')} } from './servers/${server}';`);
  }
  return imports.join('\n');
}

export { TOOL_CATALOG, TOOL_SCHEMAS };
