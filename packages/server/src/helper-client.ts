import net from 'node:net';
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import crypto from 'node:crypto';
import { spawn } from 'node:child_process';
import { DEFAULT_TIMEOUT_MS } from '../../shared/src/constants.js';
import { TOOL_RISK } from '../../shared/src/policy.js';
import type { ErrorCode, RpcRequest, RpcResponse, ToolResult } from '../../shared/src/protocol.js';
import { logError, logInfo, logWarn, PRODESK_DATA_DIR } from './logger.js';

export class HelperRpcError extends Error {
  public readonly code: ErrorCode;
  public readonly details?: unknown;

  constructor(code: ErrorCode, message: string, details?: unknown) {
    super(message);
    this.name = 'HelperRpcError';
    this.code = code;
    this.details = details;
  }
}

interface PendingCall {
  resolve: (res: ToolResult) => void;
  reject: (err: Error) => void;
  timer: NodeJS.Timeout;
  method: string;
}

export class HelperClient {
  private socket: net.Socket | null = null;
  private connected = false;
  private connectingPromise: Promise<void> | null = null;
  private buffer = '';
  private nextId = 1;
  private readonly pending = new Map<string | number, PendingCall>();
  public clientName = 'mcp-client';

  public get isConnected(): boolean {
    return this.connected;
  }

  public getTokenPath(): string {
    return path.join(PRODESK_DATA_DIR, 'helper.token');
  }

  public getPipeNameFilePath(): string {
    return path.join(PRODESK_DATA_DIR, 'pipe.name');
  }

  public ensureToken(): string {
    fs.mkdirSync(PRODESK_DATA_DIR, { recursive: true });
    const tokenPath = this.getTokenPath();
    if (fs.existsSync(tokenPath)) {
      const existing = fs.readFileSync(tokenPath, 'utf8').trim();
      if (existing.length >= 32) return existing;
    }
    const token = crypto.randomBytes(32).toString('hex');
    fs.writeFileSync(tokenPath, token, { mode: 0o600 });
    return token;
  }

  public resolvePipePath(): string {
    const pipeFile = this.getPipeNameFilePath();
    if (fs.existsSync(pipeFile)) {
      const saved = fs.readFileSync(pipeFile, 'utf8').trim();
      if (saved.startsWith('\\\\.\\pipe\\')) return saved;
      if (saved) return `\\\\.\\pipe\\${saved}`;
    }
    const user = (os.userInfo().username || 'default').replace(/[^a-zA-Z0-9_-]/g, '_').toLowerCase();
    return `\\\\.\\pipe\\prodesk-${user}`;
  }

  public resolveHelperExecutable(): string | null {
    const candidates: string[] = [];
    if (process.env.PRODESK_HELPER_PATH) {
      candidates.push(process.env.PRODESK_HELPER_PATH);
    }

    // Relative to bundled index.cjs or source
    const baseDir = typeof __dirname !== 'undefined' ? __dirname : process.cwd();
    candidates.push(
      path.join(baseDir, 'prodesk-helper.exe'),
      path.join(PRODESK_DATA_DIR, 'app', 'prodesk-helper.exe'),
      path.resolve(baseDir, '../../../native/Prodesk.Helper/bin/Publish/prodesk-helper.exe'),
      path.resolve(baseDir, '../../../native/Prodesk.Helper/bin/Release/net8.0-windows10.0.19041.0/win-x64/publish/prodesk-helper.exe'),
      path.resolve(baseDir, '../../../native/Prodesk.Helper/bin/Release/net8.0-windows10.0.19041.0/prodesk-helper.exe'),
      path.resolve(baseDir, '../../../native/Prodesk.Helper/bin/Debug/net8.0-windows10.0.19041.0/prodesk-helper.exe'),
      path.resolve(process.cwd(), 'packages/server/dist/prodesk-helper.exe'),
      path.resolve(process.cwd(), 'native/Prodesk.Helper/bin/Publish/prodesk-helper.exe'),
      path.resolve(process.cwd(), 'native/Prodesk.Helper/bin/Debug/net8.0-windows10.0.19041.0/prodesk-helper.exe'),
    );

    for (const c of candidates) {
      if (c && fs.existsSync(c)) return c;
    }
    return null;
  }

  private async spawnHelperProcess(): Promise<void> {
    this.ensureToken();
    const exe = this.resolveHelperExecutable();
    if (!exe) {
      throw new HelperRpcError(
        'failed',
        'prodesk-helper.exe bulunamadı. Lütfen önce "npm run build" çalıştırın.',
      );
    }
    logInfo(`Spawning native helper: ${exe}`);
    const child = spawn(exe, ['--background'], {
      detached: true,
      stdio: 'ignore',
      windowsHide: true,
      cwd: path.dirname(exe),
    });
    child.unref();

    // Wait up to 4.5s for pipe to become ready
    for (let i = 0; i < 30; i++) {
      await new Promise((r) => setTimeout(r, 150));
      try {
        await this.tryOpenSocket(500);
        return;
      } catch {
        // keep polling
      }
    }
    throw new HelperRpcError('timeout', 'prodesk-helper.exe başlatıldı ancak named pipe zamanında yanıt vermedi.');
  }

  private tryOpenSocket(timeoutMs: number = 1500): Promise<void> {
    return new Promise((resolve, reject) => {
      const pipePath = this.resolvePipePath();
      const token = this.ensureToken();
      let settled = false;

      const sock = net.createConnection(pipePath);
      const timer = setTimeout(() => {
        if (!settled) {
          settled = true;
          sock.destroy();
          reject(new Error(`Pipe connection timeout (${pipePath})`));
        }
      }, timeoutMs);

      sock.on('connect', () => {
        this.socket = sock;
        this.buffer = '';
        this.setupSocketListeners(sock);

        // Perform token authentication handshake
        this.sendRawRpc('auth', { token, client: this.clientName, pid: process.pid }, 3000)
          .then(() => {
            if (!settled) {
              settled = true;
              clearTimeout(timer);
              this.connected = true;
              resolve();
            }
          })
          .catch((err) => {
            if (!settled) {
              settled = true;
              clearTimeout(timer);
              sock.destroy();
              reject(err);
            }
          });
      });

      sock.on('error', (err) => {
        if (!settled) {
          settled = true;
          clearTimeout(timer);
          reject(err);
        }
      });
    });
  }

  private setupSocketListeners(sock: net.Socket): void {
    sock.on('data', (chunk: Buffer) => {
      this.buffer += chunk.toString('utf8');
      let nlIdx: number;
      while ((nlIdx = this.buffer.indexOf('\n')) !== -1) {
        const line = this.buffer.slice(0, nlIdx).trim();
        this.buffer = this.buffer.slice(nlIdx + 1);
        if (!line) continue;
        this.handleLine(line);
      }
    });

    sock.on('close', () => {
      this.connected = false;
      this.socket = null;
      for (const [, p] of this.pending) {
        clearTimeout(p.timer);
        p.reject(new HelperRpcError('failed', 'Connection to prodesk-helper.exe closed unexpectedly.'));
      }
      this.pending.clear();
    });

    sock.on('error', (err) => {
      logWarn(`Pipe socket error: ${err.message}`);
    });
  }

  private handleLine(line: string): void {
    try {
      const msg = JSON.parse(line) as RpcResponse;
      const pending = this.pending.get(msg.id);
      if (!pending) return;
      this.pending.delete(msg.id);
      clearTimeout(pending.timer);

      if (msg.error) {
        const code: ErrorCode = msg.error.data?.errorCode ?? 'failed';
        pending.reject(new HelperRpcError(code, msg.error.message, msg.error.data?.details));
      } else {
        pending.resolve(msg.result ?? { text: 'OK' });
      }
    } catch (e) {
      logError(`Failed to parse helper JSON-RPC frame: ${(e as Error).message}`);
    }
  }

  public async ensureConnected(): Promise<void> {
    if (this.connected && this.socket && !this.socket.destroyed) return;
    if (this.connectingPromise) return this.connectingPromise;

    this.connectingPromise = (async () => {
      try {
        await this.tryOpenSocket(1000);
      } catch {
        await this.spawnHelperProcess();
      }
    })().finally(() => {
      this.connectingPromise = null;
    });

    return this.connectingPromise;
  }

  private sendRawRpc(method: string, params: Record<string, unknown>, timeoutMs: number): Promise<ToolResult> {
    return new Promise((resolve, reject) => {
      if (!this.socket || this.socket.destroyed) {
        reject(new HelperRpcError('failed', 'Helper socket is not connected.'));
        return;
      }
      const id = this.nextId++;
      const req: RpcRequest = { jsonrpc: '2.0', id, method, params };
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new HelperRpcError('timeout', `Tool "${method}" timed out after ${timeoutMs} ms.`));
      }, timeoutMs);

      this.pending.set(id, { resolve, reject, timer, method });
      this.socket.write(JSON.stringify(req) + '\n', 'utf8');
    });
  }

  public async call(
    method: string,
    params: Record<string, unknown> = {},
    timeoutMs: number = DEFAULT_TIMEOUT_MS,
  ): Promise<ToolResult> {
    await this.ensureConnected();
    try {
      return await this.sendRawRpc(method, { ...params, _client: this.clientName }, timeoutMs);
    } catch (err) {
      const isReadOnly = (TOOL_RISK[method] ?? 'critical') === 'read';
      const isDisconnect =
        err instanceof HelperRpcError &&
        err.code === 'failed' &&
        err.message.includes('closed unexpectedly');
      // Crash resilience (§2.1): retry once ONLY for read tools
      if (isReadOnly && isDisconnect) {
        logWarn(`Helper disconnected during read tool "${method}", restarting and retrying once...`);
        this.connected = false;
        await this.ensureConnected();
        return await this.sendRawRpc(method, { ...params, _client: this.clientName }, timeoutMs);
      }
      throw err;
    }
  }

  public close(): void {
    this.connected = false;
    if (this.socket) {
      try {
        this.socket.destroy();
      } catch {
        // ignore
      }
      this.socket = null;
    }
  }
}
