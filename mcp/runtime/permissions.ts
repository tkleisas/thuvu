/**
 * Permission levels and security policies for MCP sandbox
 */

export enum PermissionLevel {
  /** Only read operations allowed */
  ReadOnly = 'readonly',
  /** Read and write operations in project directory */
  ReadWrite = 'readwrite',
  /** Can run processes (build, test, etc.) */
  Execute = 'execute',
  /** All permissions (requires user approval) */
  Full = 'full',
}

export interface PermissionPolicy {
  level: PermissionLevel;
  allowedPaths: string[];
  deniedPaths: string[];
  allowedCommands: string[];
  maxExecutionTime: number;
  maxMemoryMb: number;
}

export const DEFAULT_POLICY: PermissionPolicy = {
  level: PermissionLevel.ReadWrite,
  allowedPaths: ['./', './src', './tests', './docs'],
  deniedPaths: ['.git/objects', '.git/hooks', 'node_modules'],
  allowedCommands: ['dotnet', 'git', 'npm', 'node'],
  maxExecutionTime: 300000, // 5 minutes
  maxMemoryMb: 512,
};

/**
 * Get Deno permission flags based on permission level
 */
export function getDenoPermissionFlags(
  policy: PermissionPolicy,
  projectRoot: string
): string[] {
  const flags: string[] = [];

  switch (policy.level) {
    case PermissionLevel.ReadOnly:
      flags.push(`--allow-read=${projectRoot}`);
      break;
    
    case PermissionLevel.ReadWrite:
      flags.push(`--allow-read=${projectRoot}`);
      flags.push(`--allow-write=${projectRoot}`);
      break;
    
    case PermissionLevel.Execute:
      flags.push(`--allow-read=${projectRoot}`);
      flags.push(`--allow-write=${projectRoot}`);
      flags.push('--allow-run=dotnet,git,npm,node');
      break;
    
    case PermissionLevel.Full:
      flags.push('--allow-all');
      break;
  }

  // Always deny network by default (can be overridden for Full)
  if (policy.level !== PermissionLevel.Full) {
    flags.push('--deny-net');
  }

  return flags;
}

/**
 * Validate a path is within allowed paths
 */
export function isPathAllowed(
  path: string,
  policy: PermissionPolicy,
  projectRoot: string
): boolean {
  // Normalize path
  const normalizedPath = path.replace(/\\/g, '/');
  const normalizedRoot = projectRoot.replace(/\\/g, '/');

  // Must be within project root
  if (!normalizedPath.startsWith(normalizedRoot)) {
    return false;
  }

  // Check denied paths
  for (const denied of policy.deniedPaths) {
    if (normalizedPath.includes(denied)) {
      return false;
    }
  }

  return true;
}

/**
 * Validate a command is allowed
 */
export function isCommandAllowed(
  command: string,
  policy: PermissionPolicy
): boolean {
  return policy.allowedCommands.includes(command.toLowerCase());
}
