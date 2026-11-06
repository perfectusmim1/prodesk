import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import crypto from 'node:crypto';
import WebSocket from 'ws';
import { PROWSER_PORT_BASE, PROWSER_PORT_COUNT } from '../../shared/src/constants.js';
import type { ToolResult } from '../../shared/src/protocol.js';

export const PROWSER_DATA_DIR = path.join(
  process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local'),
  'Prowser',
);

export interface ProwserStatusInfo {
  reachable: boolean;
  port?: number;
  tokenFound: boolean;
  extensionConnected?: boolean;
  error?: string;
}

function readProwserToken(): string | null {
  try {
    const p = path.join(PROWSER_DATA_DIR, 'hub.token');
    if (!fs.existsSync(p)) return null;
    const t = fs.readFileSync(p, 'utf8').trim();
    return t || null;
  } catch {
    return null;
  }
}

async function connectPeerToPort(port: number, token: string, timeoutMs: number = 1500): Promise<WebSocket | null> {
  return new Promise((resolve) => {
    let settled = false;
    const ws = new WebSocket(`ws://127.0.0.1:${port}/peer`);
    const timer = setTimeout(() => {
      if (!settled) {
        settled = true;
        try {
          ws.terminate();
        } catch {
          // ignore
        }
        resolve(null);
      }
    }, timeoutMs);

    ws.on('open', () => {
      ws.send(JSON.stringify({ type: 'peer-hello', token, client: 'prodesk' }));
    });

    ws.on('message', (raw) => {
      if (settled) return;
      try {
        const msg = JSON.parse(String(raw));
        if (msg.type === 'peer-ok') {
          settled = true;
          clearTimeout(timer);
          resolve(ws);
        } else if (msg.type === 'peer-denied') {
          settled = true;
          clearTimeout(timer);
          ws.close();
          resolve(null);
        }
      } catch {
        // ignore
      }
    });

    ws.on('error', () => {
      if (!settled) {
        settled = true;
        clearTimeout(timer);
        resolve(null);
      }
    });

    ws.on('close', () => {
      if (!settled) {
        settled = true;
        clearTimeout(timer);
        resolve(null);
      }
    });
  });
}

export async function checkProwserReachable(): Promise<ProwserStatusInfo> {
  const token = readProwserToken();
  if (!token) {
    return {
      reachable: false,
      tokenFound: false,
      error: 'Prowser hub.token not found at %LOCALAPPDATA%\\Prowser\\hub.token',
    };
  }

  for (let i = 0; i < PROWSER_PORT_COUNT; i++) {
    const port = PROWSER_PORT_BASE + i;
    const ws = await connectPeerToPort(port, token, 500);
    if (ws) {
      try {
        const res = await sendProwserRequestOnSocket(ws, 'prowser_status', {}, 2500);
        ws.close();
        const extConn = (res.data as Record<string, unknown> | undefined)?.extensionConnected;
        return {
          reachable: true,
          port,
          tokenFound: true,
          extensionConnected: typeof extConn === 'boolean' ? extConn : true,
        };
      } catch {
        ws.close();
        return { reachable: true, port, tokenFound: true };
      }
    }
  }

  return {
    reachable: false,
    tokenFound: true,
    error: 'No active Prowser hub listening on ports 17890..17899',
  };
}

function sendProwserRequestOnSocket(
  ws: WebSocket,
  tool: string,
  params: Record<string, unknown>,
  timeoutMs: number,
): Promise<ToolResult> {
  return new Promise((resolve, reject) => {
    const id = crypto.randomUUID();
    const timer = setTimeout(() => {
      cleanup();
      reject(new Error(`[timeout] Prowser tool "${tool}" timed out after ${timeoutMs} ms.`));
    }, timeoutMs);

    const onMessage = (raw: WebSocket.RawData) => {
      try {
        const msg = JSON.parse(String(raw));
        if (msg.type === 'res' && msg.id === id) {
          cleanup();
          if (msg.ok) {
            resolve(msg.result ?? { text: 'OK' });
          } else {
            const code = msg.error?.code ?? 'failed';
            const errMsg = msg.error?.message ?? 'Prowser request failed';
            reject(new Error(`[${code}] ${errMsg}`));
          }
        }
      } catch {
        // ignore non-matching frames
      }
    };

    const onError = (err: Error) => {
      cleanup();
      reject(new Error(`[failed] Prowser peer socket error: ${err.message}`));
    };

    const onClose = () => {
      cleanup();
      reject(new Error('[failed] Prowser peer socket closed unexpectedly.'));
    };

    const cleanup = () => {
      clearTimeout(timer);
      ws.off('message', onMessage);
      ws.off('error', onError);
      ws.off('close', onClose);
    };

    ws.on('message', onMessage);
    ws.on('error', onError);
    ws.on('close', onClose);

    ws.send(JSON.stringify({ type: 'req', id, client: 'prodesk', tool, params }));
  });
}

export async function callProwserPeer(
  tool: string,
  params: Record<string, unknown> = {},
  timeoutMs: number = 45_000,
): Promise<ToolResult> {
  const token = readProwserToken();
  if (!token) {
    throw new Error(
      '[failed] Prowser çalışmıyor (%LOCALAPPDATA%\\Prowser\\hub.token bulunamadı). Lütfen Edge/Chrome tarayıcısını veya prowser MCP sunucusunu başlatın.',
    );
  }

  for (let i = 0; i < PROWSER_PORT_COUNT; i++) {
    const port = PROWSER_PORT_BASE + i;
    const ws = await connectPeerToPort(port, token, 800);
    if (ws) {
      try {
        return await sendProwserRequestOnSocket(ws, tool, params, timeoutMs);
      } finally {
        try {
          ws.close();
        } catch {
          // ignore
        }
      }
    }
  }

  throw new Error(
    '[failed] Prowser çalışmıyor (127.0.0.1:17890..17899 üzerinde aktif hub bulunamadı). Edge tarayıcısını açın veya prowser MCP aracını başlatın.',
  );
}
