import fs from 'node:fs';
import path from 'node:path';
import { PRODESK_DATA_DIR } from './logger.js';
import type { ActionVerification, ToolResult } from '../../shared/src/protocol.js';

export interface SessionActionEntry {
  index: number;
  timestamp: string;
  tool: string;
  paramsSummary: string;
  durationMs: number;
  ok: boolean;
  resultSummary: string;
  verification?: ActionVerification;
  thumbnailBase64?: string;
}

export class SessionRecorder {
  private readonly sessionId: string;
  private readonly sessionDir: string;
  private readonly entries: SessionActionEntry[] = [];

  constructor() {
    const now = new Date().toISOString().replace(/[:.]/g, '-');
    this.sessionId = `session-${now}`;
    this.sessionDir = path.join(PRODESK_DATA_DIR, 'sessions', now.slice(0, 10));
  }

  public record(entry: Omit<SessionActionEntry, 'index' | 'timestamp'>): void {
    const item: SessionActionEntry = {
      index: this.entries.length + 1,
      timestamp: new Date().toISOString(),
      ...entry,
    };
    this.entries.push(item);
    if (this.entries.length > 500) {
      this.entries.shift();
    }
    try {
      fs.mkdirSync(this.sessionDir, { recursive: true });
      fs.appendFileSync(
        path.join(this.sessionDir, `${this.sessionId}.jsonl`),
        JSON.stringify({ ...item, thumbnailBase64: undefined }) + '\n',
        'utf8',
      );
    } catch {
      // ignore disk errors
    }
  }

  public generateHtmlReport(customSavePath?: string): ToolResult {
    fs.mkdirSync(this.sessionDir, { recursive: true });
    const outPath = customSavePath
      ? path.resolve(customSavePath)
      : path.join(this.sessionDir, `${this.sessionId}-report.html`);

    fs.mkdirSync(path.dirname(outPath), { recursive: true });

    const rowsHtml = this.entries
      .map((e) => {
        const badgeColor = e.ok ? '#10b981' : '#ef4444';
        const viaBadge = e.verification?.via ? `<span class="via">via: ${escapeHtml(e.verification.via)}</span>` : '';
        const thumb = e.thumbnailBase64
          ? `<img class="thumb" src="data:image/jpeg;base64,${e.thumbnailBase64}" alt="snapshot" />`
          : '';
        return `<div class="card">
          <div class="header">
            <span class="idx">#${e.index}</span>
            <strong class="tool">${escapeHtml(e.tool)}</strong>
            ${viaBadge}
            <span class="status" style="background:${badgeColor}">${e.ok ? 'OK' : 'ERR'}</span>
            <span class="meta">${e.durationMs} ms · ${escapeHtml(e.timestamp)}</span>
          </div>
          <div class="body">
            <pre class="params">${escapeHtml(e.paramsSummary)}</pre>
            <pre class="result">${escapeHtml(e.resultSummary)}</pre>
            ${thumb}
          </div>
        </div>`;
      })
      .join('\n');

    const html = `<!DOCTYPE html>
<html lang="tr">
<head>
<meta charset="utf-8" />
<title>Prodesk Oturum Raporu (${escapeHtml(this.sessionId)})</title>
<style>
  body { font-family: system-ui, -apple-system, Segoe UI, sans-serif; background: #0f172a; color: #f8fafc; margin: 0; padding: 24px; }
  h1 { font-size: 20px; margin-bottom: 16px; }
  .card { background: #1e293b; border: 1px solid #334155; border-radius: 10px; padding: 14px; margin-bottom: 12px; }
  .header { display: flex; align-items: center; gap: 10px; margin-bottom: 8px; }
  .idx { color: #94a3b8; font-weight: 600; }
  .tool { color: #38bdf8; font-size: 15px; }
  .via { background: #334155; color: #e2e8f0; padding: 2px 8px; border-radius: 999px; font-size: 12px; }
  .status { color: #fff; padding: 2px 8px; border-radius: 6px; font-size: 11px; font-weight: 700; }
  .meta { margin-left: auto; color: #94a3b8; font-size: 12px; }
  pre { background: #0f172a; padding: 8px 10px; border-radius: 6px; overflow-x: auto; font-size: 12px; margin: 4px 0; color: #cbd5e1; }
  .thumb { max-width: 320px; border-radius: 6px; margin-top: 8px; border: 1px solid #475569; }
</style>
</head>
<body>
  <h1>Prodesk Oturum Zaman Çizelgesi — ${this.entries.length} Eylem</h1>
  ${rowsHtml || '<p>Henüz kaydedilmiş eylem yok.</p>'}
</body>
</html>`;

    fs.writeFileSync(outPath, html, 'utf8');
    return {
      text: `Session HTML report generated at ${outPath} (${this.entries.length} actions).`,
      data: { path: outPath, actionCount: this.entries.length, sessionId: this.sessionId },
    };
  }
}

function escapeHtml(s: string): string {
  return s
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}
