import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';

export const PRODESK_DATA_DIR = path.join(
  process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local'),
  'Prodesk',
);

const LOG_DIR = path.join(PRODESK_DATA_DIR, 'logs');

function ensureLogDir(): void {
  try {
    fs.mkdirSync(LOG_DIR, { recursive: true });
  } catch {
    // ignore
  }
}

export function logInfo(msg: string): void {
  writeLog('INFO', msg);
}

export function logWarn(msg: string): void {
  writeLog('WARN', msg);
}

export function logError(msg: string): void {
  writeLog('ERROR', msg);
}

function writeLog(level: 'INFO' | 'WARN' | 'ERROR', msg: string): void {
  const ts = new Date().toISOString();
  const line = `[${ts}] [prodesk-server] [${level}] ${msg}\n`;
  // NEVER write to stdout in an MCP stdio server.
  try {
    process.stderr.write(line);
  } catch {
    // ignore
  }
  try {
    ensureLogDir();
    const day = ts.slice(0, 10);
    fs.appendFileSync(path.join(LOG_DIR, `server-${day}.log`), line, 'utf8');
  } catch {
    // ignore
  }
}
