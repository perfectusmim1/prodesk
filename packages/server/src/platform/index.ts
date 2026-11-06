import type { ToolResult } from '../../../shared/src/protocol.js';
import { HelperClient, HelperRpcError } from '../helper-client.js';
import { WindowsPlatform } from './windows.js';

export interface PlatformProvider {
  readonly platformName: string;
  readonly isSupported: boolean;
  execute(tool: string, params: Record<string, unknown>, timeoutMs?: number): Promise<ToolResult>;
  close(): void;
}

class UnsupportedPlatform implements PlatformProvider {
  public readonly platformName = process.platform;
  public readonly isSupported = false;

  public async execute(tool: string): Promise<ToolResult> {
    throw new HelperRpcError(
      'unsupported_platform',
      `Prodesk tool "${tool}" requires Windows 10/11 (win32). Current OS platform "${process.platform}" is not supported.`,
    );
  }

  public close(): void {
    // no-op
  }
}

export function createPlatform(helperClient: HelperClient): PlatformProvider {
  if (process.platform === 'win32') {
    return new WindowsPlatform(helperClient);
  }
  return new UnsupportedPlatform();
}
