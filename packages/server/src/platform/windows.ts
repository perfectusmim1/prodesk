import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import crypto from 'node:crypto';
import { spawn, type ChildProcess } from 'node:child_process';
import { DEFAULT_TIMEOUT_MS, MAX_OUTPUT_CHARS } from '../../../shared/src/constants.js';
import { expandEnvVars } from '../../../shared/src/policy.js';
import type { ToolResult } from '../../../shared/src/protocol.js';
import { HelperClient, HelperRpcError } from '../helper-client.js';
import { PRODESK_DATA_DIR } from '../logger.js';
import type { PlatformProvider } from './index.js';

interface BackgroundCommandJob {
  id: string;
  command: string;
  shell: string;
  startedAt: string;
  process: ChildProcess;
  stdout: string;
  stderr: string;
  exitCode: number | null;
  exited: boolean;
  outputFilePath: string;
}

export class WindowsPlatform implements PlatformProvider {
  public readonly platformName = 'win32';
  public readonly isSupported = true;
  private readonly helper: HelperClient;
  private readonly jobs = new Map<string, BackgroundCommandJob>();

  constructor(helper: HelperClient) {
    this.helper = helper;
  }

  public close(): void {
    for (const [, job] of this.jobs) {
      if (!job.exited && job.process.pid) {
        try {
          spawn('taskkill', ['/PID', String(job.process.pid), '/T', '/F'], { windowsHide: true });
        } catch {
          // ignore
        }
      }
    }
    this.helper.close();
  }

  /**
   * Authorizes an action with the C# Helper (which enforces Policy, Kill Switch, Rate Limit,
   * Auto-Approve, WPF Confirmation Dialog, and Audit Logging) before executing any hybrid tool.
   */
  private async authorizeWithHelper(
    tool: string,
    params: Record<string, unknown>,
    extra?: { isUnsignedInstaller?: boolean; targetProcess?: string; targetTitle?: string },
  ): Promise<void> {
    await this.helper.call(
      'authorize_tool',
      {
        tool,
        params,
        ...extra,
      },
      70_000, // allow 60s for user confirmation dialog
    );
  }

  private async recordAuditInHelper(
    tool: string,
    durationMs: number,
    ok: boolean,
    errorMsg?: string,
  ): Promise<void> {
    try {
      await this.helper.call('audit_complete', { tool, durationMs, ok, error: errorMsg }, 3000);
    } catch {
      // ignore
    }
  }

  public async execute(
    tool: string,
    params: Record<string, unknown>,
    timeoutMs: number = DEFAULT_TIMEOUT_MS,
  ): Promise<ToolResult> {
    // Hybrid Node+Win32 tools (authorized and audited by Helper, executed with high-speed streaming/HTTP/process handling)
    if (tool in this.localHandlers) {
      const t0 = Date.now();
      let isUnsignedInstaller = false;
      if (tool === 'run_installer' && typeof params.path === 'string') {
        const sigRes = await this.helper.call('verify_file', { path: expandEnvVars(params.path) }, 15_000);
        const isSigned = Boolean((sigRes.data as Record<string, unknown> | undefined)?.isSigned);
        isUnsignedInstaller = !isSigned;
      }

      await this.authorizeWithHelper(tool, params, { isUnsignedInstaller });
      try {
        const res = await this.localHandlers[tool](params, timeoutMs);
        await this.recordAuditInHelper(tool, Date.now() - t0, true);
        return res;
      } catch (err) {
        await this.recordAuditInHelper(tool, Date.now() - t0, false, (err as Error).message);
        throw err;
      }
    }

    // All UIA, OCR, Screenshot, Input, Window/App, Clipboard, Recording, Verify, and Status tools run inside C# Helper
    return this.helper.call(tool, params, timeoutMs);
  }

  private readonly localHandlers: Record<
    string,
    (params: Record<string, unknown>, timeoutMs: number) => Promise<ToolResult>
  > = {
    // -----------------------------------------------------------------------
    // 5.5 File System
    // -----------------------------------------------------------------------
    fs_list: async (params) => {
      const dirPath = path.resolve(expandEnvVars(String(params.path)));
      if (!fs.existsSync(dirPath)) throw new HelperRpcError('not_found', `Directory not found: ${dirPath}`);
      const maxItems = Number(params.max_items ?? 250);
      const recursive = Boolean(params.recursive);
      const pattern = typeof params.pattern === 'string' ? params.pattern.trim() : '';
      const regex = pattern
        ? new RegExp('^' + pattern.split('*').map((s) => s.replace(/[.+?^${}()|[\]\\]/g, '\\$&')).join('.*') + '$', 'i')
        : null;

      const items: Array<{ name: string; path: string; type: 'file' | 'dir'; size: number; mtime: string }> = [];
      const walk = (curr: string) => {
        if (items.length >= maxItems) return;
        let entries: fs.Dirent[];
        try {
          entries = fs.readdirSync(curr, { withFileTypes: true });
        } catch {
          return;
        }
        for (const entry of entries) {
          if (items.length >= maxItems) break;
          const full = path.join(curr, entry.name);
          const isDir = entry.isDirectory();
          if (!regex || regex.test(entry.name)) {
            let size = 0;
            let mtime = '';
            try {
              const st = fs.statSync(full);
              size = isDir ? 0 : st.size;
              mtime = st.mtime.toISOString();
            } catch {
              // ignore
            }
            items.push({ name: entry.name, path: full, type: isDir ? 'dir' : 'file', size, mtime });
          }
          if (isDir && recursive) walk(full);
        }
      };
      walk(dirPath);

      const text = items
        .map((i) => `${i.type === 'dir' ? '[DIR] ' : '      '}${i.name} (${i.size} B) — ${i.path}`)
        .join('\n');
      return { text: text || '(empty directory)', data: { path: dirPath, count: items.length, items }, untrusted: true };
    },

    fs_info: async (params) => {
      const target = path.resolve(expandEnvVars(String(params.path)));
      if (!fs.existsSync(target)) throw new HelperRpcError('not_found', `Path not found: ${target}`);
      const st = fs.statSync(target);
      const realPath = fs.realpathSync(target);
      let sha256: string | undefined;
      if (st.isFile() && st.size <= 104_857_600) {
        const hash = crypto.createHash('sha256');
        hash.update(fs.readFileSync(realPath));
        sha256 = hash.digest('hex');
      }
      const info = {
        path: target,
        realPath,
        type: st.isDirectory() ? 'directory' : 'file',
        size: st.size,
        createdAt: st.birthtime.toISOString(),
        modifiedAt: st.mtime.toISOString(),
        sha256,
      };
      return { text: JSON.stringify(info, null, 2), data: info };
    },

    fs_read: async (params) => {
      const target = path.resolve(expandEnvVars(String(params.path)));
      if (!fs.existsSync(target)) throw new HelperRpcError('not_found', `File not found: ${target}`);
      const maxBytes = Number(params.max_bytes ?? 524_288);
      const encoding = String(params.encoding ?? 'auto');
      const buf = fs.readFileSync(target);
      const slice = buf.byteLength > maxBytes ? buf.subarray(0, maxBytes) : buf;

      if (encoding === 'base64') {
        const b64 = slice.toString('base64');
        return { text: b64, data: { path: target, bytesRead: slice.byteLength, truncated: buf.byteLength > maxBytes } };
      }

      let decoded: string;
      if (encoding === 'utf16le' || (encoding === 'auto' && slice.length >= 2 && slice[0] === 0xff && slice[1] === 0xfe)) {
        decoded = new TextDecoder('utf-16le').decode(slice);
      } else if (encoding === 'windows1254') {
        decoded = new TextDecoder('windows-1254').decode(slice);
      } else {
        // Auto-detect UTF-8 vs Windows-1254 if invalid UTF-8 sequences exist
        try {
          decoded = new TextDecoder('utf-8', { fatal: true }).decode(slice);
        } catch {
          decoded = new TextDecoder('windows-1254').decode(slice);
        }
      }
      // Strip UTF-8 BOM if present
      if (decoded.charCodeAt(0) === 0xfeff) decoded = decoded.slice(1);
      return {
        text: decoded,
        untrusted: true,
        data: { path: target, bytesRead: slice.byteLength, totalBytes: buf.byteLength, truncated: buf.byteLength > maxBytes },
      };
    },

    fs_write: async (params) => {
      const target = path.resolve(expandEnvVars(String(params.path)));
      const content = String(params.content ?? '');
      const encoding = String(params.encoding ?? 'utf8');
      const append = Boolean(params.append);

      fs.mkdirSync(path.dirname(target), { recursive: true });
      const data = encoding === 'base64' ? Buffer.from(content, 'base64') : Buffer.from(content, 'utf8');

      if (append) {
        fs.appendFileSync(target, data);
      } else {
        // Atomic write via temp file + rename
        const tmp = `${target}.prodesk-tmp-${process.pid}-${Date.now()}`;
        fs.writeFileSync(tmp, data);
        fs.renameSync(tmp, target);
      }
      return {
        text: `Wrote ${data.byteLength} bytes to ${target}`,
        data: { path: target, bytesWritten: data.byteLength, append },
      };
    },

    fs_copy: async (params) => {
      const src = path.resolve(expandEnvVars(String(params.source)));
      const dest = path.resolve(expandEnvVars(String(params.destination)));
      const overwrite = params.overwrite !== false;
      if (!fs.existsSync(src)) throw new HelperRpcError('not_found', `Source not found: ${src}`);
      fs.mkdirSync(path.dirname(dest), { recursive: true });
      fs.cpSync(src, dest, { recursive: true, force: overwrite, errorOnExist: !overwrite });
      return { text: `Copied ${src} -> ${dest}`, data: { source: src, destination: dest } };
    },

    fs_move: async (params) => {
      const src = path.resolve(expandEnvVars(String(params.source)));
      const dest = path.resolve(expandEnvVars(String(params.destination)));
      const overwrite = Boolean(params.overwrite);
      if (!fs.existsSync(src)) throw new HelperRpcError('not_found', `Source not found: ${src}`);
      if (fs.existsSync(dest) && !overwrite) {
        throw new HelperRpcError('bad_request', `Destination already exists: ${dest}`);
      }
      fs.mkdirSync(path.dirname(dest), { recursive: true });
      try {
        fs.renameSync(src, dest);
      } catch {
        fs.cpSync(src, dest, { recursive: true, force: true });
        fs.rmSync(src, { recursive: true, force: true });
      }
      return { text: `Moved ${src} -> ${dest}`, data: { source: src, destination: dest } };
    },

    fs_mkdir: async (params) => {
      const dir = path.resolve(expandEnvVars(String(params.path)));
      fs.mkdirSync(dir, { recursive: true });
      return { text: `Created directory ${dir}`, data: { path: dir } };
    },

    fs_delete: async (params) => {
      const target = path.resolve(expandEnvVars(String(params.path)));
      const permanent = Boolean(params.permanent);
      if (!fs.existsSync(target)) throw new HelperRpcError('not_found', `Path not found: ${target}`);
      if (permanent) {
        fs.rmSync(target, { recursive: true, force: true });
        return { text: `Permanently deleted ${target}`, data: { path: target, permanent: true } };
      }
      // Delegate Recycle Bin deletion to C# Helper (SHFileOperation / FileSystem.DeleteFile SendToRecycleBin)
      return this.helper.call('fs_recycle', { path: target });
    },

    fs_search: async (params) => {
      const rootDir = path.resolve(expandEnvVars(String(params.path)));
      if (!fs.existsSync(rootDir)) throw new HelperRpcError('not_found', `Directory not found: ${rootDir}`);
      const namePattern = typeof params.name_pattern === 'string' ? params.name_pattern.trim() : '';
      const contentPattern = typeof params.content_pattern === 'string' ? params.content_pattern.trim() : '';
      const maxResults = Number(params.max_results ?? 100);

      const nameRe = namePattern
        ? new RegExp(namePattern.split('*').map((s) => s.replace(/[.+?^${}()|[\]\\]/g, '\\$&')).join('.*'), 'i')
        : null;
      const contentRe = contentPattern ? new RegExp(contentPattern, 'i') : null;

      const results: Array<{ path: string; size: number; matchedLine?: string }> = [];
      const walk = (dir: string) => {
        if (results.length >= maxResults) return;
        let entries: fs.Dirent[];
        try {
          entries = fs.readdirSync(dir, { withFileTypes: true });
        } catch {
          return;
        }
        for (const e of entries) {
          if (results.length >= maxResults) break;
          if (e.name === 'node_modules' || e.name === '.git' || e.name === 'bin' || e.name === 'obj') continue;
          const full = path.join(dir, e.name);
          if (e.isDirectory()) {
            walk(full);
          } else if (e.isFile()) {
            if (nameRe && !nameRe.test(e.name)) continue;
            let st: fs.Stats;
            try {
              st = fs.statSync(full);
            } catch {
              continue;
            }
            if (contentRe) {
              if (st.size > 5_242_880) continue; // skip > 5MB for text search
              try {
                const txt = fs.readFileSync(full, 'utf8');
                const m = txt.match(contentRe);
                if (!m) continue;
                const line = txt.split(/\r?\n/).find((l) => contentRe.test(l))?.trim().slice(0, 200);
                results.push({ path: full, size: st.size, matchedLine: line });
              } catch {
                continue;
              }
            } else {
              results.push({ path: full, size: st.size });
            }
          }
        }
      };
      walk(rootDir);

      return {
        text: results.length
          ? results.map((r) => `${r.path} (${r.size} B)${r.matchedLine ? ' -> ' + r.matchedLine : ''}`).join('\n')
          : 'No matching files found.',
        data: { count: results.length, results },
        untrusted: true,
      };
    },

    reveal_in_explorer: async (params) => {
      const target = path.resolve(expandEnvVars(String(params.path)));
      if (!fs.existsSync(target)) throw new HelperRpcError('not_found', `Path not found: ${target}`);
      spawn('explorer.exe', [`/select,${target}`], { detached: true, stdio: 'ignore', windowsHide: false }).unref();
      return { text: `Revealed in File Explorer: ${target}`, data: { path: target } };
    },

    // -----------------------------------------------------------------------
    // 5.6 Downloads & Package Installation
    // -----------------------------------------------------------------------
    download_file: async (params) => {
      const rawUrl = String(params.url);
      let parsedUrl: URL;
      try {
        parsedUrl = new URL(rawUrl);
      } catch {
        throw new HelperRpcError('bad_request', `Invalid URL: ${rawUrl}`);
      }
      if (parsedUrl.protocol !== 'https:' && parsedUrl.protocol !== 'http:') {
        throw new HelperRpcError('blocked', `Only http/https protocols are allowed (got ${parsedUrl.protocol}).`);
      }
      // Block SSRF to loopback / link-local metadata endpoints
      const host = parsedUrl.hostname.toLowerCase();
      if (
        host === 'localhost' ||
        host === '127.0.0.1' ||
        host === '::1' ||
        host === '169.254.169.254' ||
        host.startsWith('169.254.')
      ) {
        throw new HelperRpcError('blocked', `SSRF protection blocked download from local/metadata address: ${host}`);
      }

      const downloadsDir = path.join(os.homedir(), 'Downloads');
      const rawDest = typeof params.destination === 'string' ? expandEnvVars(params.destination) : downloadsDir;
      const urlFilename = path.basename(parsedUrl.pathname) || `download-${Date.now()}.bin`;
      const filename = typeof params.filename === 'string' && params.filename.trim() ? params.filename.trim() : urlFilename;

      let outFile: string;
      if (fs.existsSync(rawDest) && fs.statSync(rawDest).isDirectory()) {
        outFile = path.join(rawDest, filename);
      } else if (rawDest.endsWith('\\') || rawDest.endsWith('/')) {
        outFile = path.join(rawDest, filename);
      } else if (typeof params.filename === 'string') {
        outFile = path.join(rawDest, filename);
      } else {
        outFile = path.resolve(rawDest);
      }

      fs.mkdirSync(path.dirname(outFile), { recursive: true });
      const maxBytes = Number(params.max_bytes ?? 1_073_741_824);

      const response = await fetch(rawUrl, {
        redirect: 'follow',
        headers: { 'User-Agent': 'Prodesk/1.0 (Windows NT 10.0; Win64; x64)' },
      });
      if (!response.ok) {
        throw new HelperRpcError('failed', `HTTP ${response.status} ${response.statusText} downloading ${rawUrl}`);
      }

      const arrayBuf = await response.arrayBuffer();
      const buf = Buffer.from(arrayBuf);
      if (buf.byteLength > maxBytes) {
        throw new HelperRpcError('bad_request', `Downloaded size (${buf.byteLength}) exceeds max_bytes (${maxBytes}).`);
      }

      const sha256 = crypto.createHash('sha256').update(buf).digest('hex');
      if (typeof params.expected_sha256 === 'string' && params.expected_sha256.trim()) {
        const expected = params.expected_sha256.trim().toLowerCase();
        if (sha256 !== expected) {
          throw new HelperRpcError(
            'failed',
            `SHA256 mismatch! Expected ${expected} but computed ${sha256}. File was not saved.`,
          );
        }
      }

      fs.writeFileSync(outFile, buf);
      return {
        text: `Downloaded ${buf.byteLength} bytes to ${outFile}\nSHA256: ${sha256}`,
        data: { path: outFile, bytes: buf.byteLength, sha256, url: rawUrl },
      };
    },

    github_release: async (params, timeoutMs) => {
      const repo = String(params.repo).trim();
      if (!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(repo)) {
        throw new HelperRpcError('bad_request', `Invalid GitHub repo format "${repo}". Expected "owner/repo".`);
      }
      const tag = typeof params.tag === 'string' && params.tag.trim() ? params.tag.trim() : 'latest';
      const apiPath = tag === 'latest' ? `releases/latest` : `releases/tags/${encodeURIComponent(tag)}`;
      const apiUrl = `https://api.github.com/repos/${repo}/${apiPath}`;

      const headers: Record<string, string> = {
        Accept: 'application/vnd.github+json',
        'User-Agent': 'Prodesk/1.0',
      };
      if (process.env.GITHUB_TOKEN) {
        headers.Authorization = `Bearer ${process.env.GITHUB_TOKEN}`;
      }

      const res = await fetch(apiUrl, { headers });
      if (!res.ok) {
        throw new HelperRpcError('failed', `GitHub API returned ${res.status} for ${repo} (${tag}).`);
      }
      const release = (await res.json()) as {
        tag_name: string;
        name: string;
        assets: Array<{ name: string; browser_download_url: string; size: number }>;
      };

      const assets = release.assets ?? [];
      const pattern = typeof params.asset_pattern === 'string' ? params.asset_pattern.trim() : '';
      const assetRe = pattern
        ? new RegExp('^' + pattern.split('*').map((s) => s.replace(/[.+?^${}()|[\]\\]/g, '\\$&')).join('.*') + '$', 'i')
        : /\.(?:exe|msi|msix|zip)$/i;

      const matched = assets.find((a) => assetRe.test(a.name)) ?? assets[0];
      if (!matched) {
        throw new HelperRpcError('not_found', `No release asset matched pattern "${pattern || '*.exe|*.msi|*.zip'}" in ${repo}@${release.tag_name}.`);
      }

      if (params.download === false) {
        return {
          text: `Matched asset: ${matched.name} (${matched.size} B) in ${repo}@${release.tag_name}\nURL: ${matched.browser_download_url}`,
          data: {
            repo,
            tag: release.tag_name,
            matchedAsset: matched,
            allAssets: assets.map((a) => ({ name: a.name, size: a.size, url: a.browser_download_url })),
          },
        };
      }

      const dlRes = await this.localHandlers.download_file(
        {
          url: matched.browser_download_url,
          destination: params.destination,
          filename: matched.name,
        },
        timeoutMs,
      );

      return {
        text: `GitHub Release ${repo}@${release.tag_name} -> ${matched.name}\n${dlRes.text}`,
        data: {
          repo,
          tag: release.tag_name,
          asset: matched.name,
          ...(dlRes.data as object),
        },
      };
    },

    winget_search: async (params, timeoutMs) => {
      const query = String(params.query).trim();
      const maxResults = Number(params.max_results ?? 20);
      const out = await this.runProcessCapture(
        'winget.exe',
        ['search', query, '--accept-source-agreements', '-n', String(maxResults)],
        timeoutMs,
      );
      return { text: out.stdout || out.stderr, data: { exitCode: out.exitCode, query }, untrusted: true };
    },

    winget_list: async (params, timeoutMs) => {
      const args = ['list', '--accept-source-agreements'];
      if (typeof params.query === 'string' && params.query.trim()) {
        args.push(params.query.trim());
      }
      const out = await this.runProcessCapture('winget.exe', args, timeoutMs);
      return { text: out.stdout || out.stderr, data: { exitCode: out.exitCode }, untrusted: true };
    },

    winget_install: async (params, timeoutMs) => {
      const id = String(params.id).trim();
      const args = [
        'install',
        '--id',
        id,
        '--exact',
        '--silent',
        '--accept-package-agreements',
        '--accept-source-agreements',
        '--disable-interactivity',
      ];
      if (typeof params.version === 'string' && params.version.trim()) {
        args.push('--version', params.version.trim());
      }
      if (typeof params.scope === 'string') {
        args.push('--scope', params.scope);
      }
      const out = await this.runProcessCapture('winget.exe', args, timeoutMs);
      if (out.exitCode !== 0) {
        throw new HelperRpcError('failed', `winget install failed (exit ${out.exitCode}):\n${out.stdout}\n${out.stderr}`);
      }
      return { text: out.stdout || `Installed ${id} via winget.`, data: { id, exitCode: out.exitCode } };
    },

    winget_uninstall: async (params, timeoutMs) => {
      const id = String(params.id).trim();
      const args = ['uninstall', '--id', id, '--exact', '--silent', '--accept-source-agreements', '--disable-interactivity'];
      const out = await this.runProcessCapture('winget.exe', args, timeoutMs);
      if (out.exitCode !== 0) {
        throw new HelperRpcError('failed', `winget uninstall failed (exit ${out.exitCode}):\n${out.stdout}\n${out.stderr}`);
      }
      return { text: out.stdout || `Uninstalled ${id} via winget.`, data: { id, exitCode: out.exitCode } };
    },

    run_installer: async (params, timeoutMs) => {
      const installerPath = path.resolve(expandEnvVars(String(params.path)));
      if (!fs.existsSync(installerPath)) {
        throw new HelperRpcError('not_found', `Installer file not found: ${installerPath}`);
      }
      const verifyRes = await this.helper.call('verify_file', { path: installerPath }, 15_000);
      const ext = path.extname(installerPath).toLowerCase();
      const customArgs = Array.isArray(params.args) ? (params.args as string[]).map(String) : undefined;
      const silent = params.silent !== false;

      let cmd: string;
      let args: string[];
      if (ext === '.msi') {
        cmd = 'msiexec.exe';
        args = customArgs ?? (silent ? ['/i', installerPath, '/qn', '/norestart'] : ['/i', installerPath]);
      } else {
        cmd = installerPath;
        args = customArgs ?? (silent ? ['/S', '/quiet', '/norestart'] : []);
      }

      const out = await this.runProcessCapture(cmd, args, Number(params.timeout_ms ?? timeoutMs));
      return {
        text: `Installer ${ path.basename(installerPath) } finished with exit code ${out.exitCode}.\nSignature: ${JSON.stringify(verifyRes.data)}`,
        data: {
          path: installerPath,
          exitCode: out.exitCode,
          signature: verifyRes.data,
          stdout: out.stdout,
          stderr: out.stderr,
        },
      };
    },

    // -----------------------------------------------------------------------
    // 5.8 Shell & Processes
    // -----------------------------------------------------------------------
    run_command: async (params) => {
      const command = String(params.command);
      const shell = String(params.shell ?? 'powershell');
      const cwd = typeof params.cwd === 'string' ? path.resolve(expandEnvVars(params.cwd)) : process.cwd();
      const timeoutMs = Number(params.timeout_ms ?? 60_000);
      const background = Boolean(params.background);
      const extraEnv = (params.env as Record<string, string> | undefined) ?? {};

      let bin: string;
      let args: string[];
      if (shell === 'cmd') {
        bin = 'cmd.exe';
        args = ['/d', '/s', '/c', `chcp 65001 >nul & ${command}`];
      } else {
        bin = shell === 'pwsh' ? 'pwsh.exe' : 'powershell.exe';
        const utf8Preamble =
          '[Console]::InputEncoding = [Console]::OutputEncoding = $OutputEncoding = [System.Text.UTF8Encoding]::new($false); ';
        args = ['-NoProfile', '-NonInteractive', '-Command', utf8Preamble + command];
      }

      if (background) {
        const jobId = `job_${Date.now().toString(36)}_${Math.random().toString(36).slice(2, 6)}`;
        const outDir = path.join(PRODESK_DATA_DIR, 'command-outputs');
        fs.mkdirSync(outDir, { recursive: true });
        const outputFilePath = path.join(outDir, `${jobId}.log`);

        const child = spawn(bin, args, {
          cwd,
          env: { ...process.env, ...extraEnv },
          windowsHide: true,
          stdio: ['pipe', 'pipe', 'pipe'],
        });

        const job: BackgroundCommandJob = {
          id: jobId,
          command,
          shell,
          startedAt: new Date().toISOString(),
          process: child,
          stdout: '',
          stderr: '',
          exitCode: null,
          exited: false,
          outputFilePath,
        };

        child.stdout?.on('data', (d: Buffer) => {
          const s = d.toString('utf8');
          job.stdout += s;
          try {
            fs.appendFileSync(outputFilePath, s, 'utf8');
          } catch {
            // ignore
          }
        });
        child.stderr?.on('data', (d: Buffer) => {
          const s = d.toString('utf8');
          job.stderr += s;
          try {
            fs.appendFileSync(outputFilePath, s, 'utf8');
          } catch {
            // ignore
          }
        });
        child.on('close', (code) => {
          job.exitCode = code;
          job.exited = true;
        });

        this.jobs.set(jobId, job);
        return {
          text: `Background command started with job_id="${jobId}" (PID: ${child.pid}).`,
          data: { job_id: jobId, pid: child.pid, outputFilePath },
        };
      }

      const out = await this.runProcessCapture(bin, args, timeoutMs, cwd, extraEnv);
      let combined = (out.stdout + (out.stderr ? `\n[STDERR]\n${out.stderr}` : '')).trim();
      let fullOutputPath: string | undefined;

      if (combined.length > MAX_OUTPUT_CHARS) {
        const outDir = path.join(PRODESK_DATA_DIR, 'command-outputs');
        fs.mkdirSync(outDir, { recursive: true });
        fullOutputPath = path.join(outDir, `cmd-${Date.now()}.log`);
        fs.writeFileSync(fullOutputPath, combined, 'utf8');
        const half = Math.floor(MAX_OUTPUT_CHARS / 2);
        combined =
          combined.slice(0, half) +
          `\n\n... [truncated ${combined.length - MAX_OUTPUT_CHARS} chars; full output saved to ${fullOutputPath}] ...\n\n` +
          combined.slice(-half);
      }

      return {
        text: combined || `(exit code ${out.exitCode})`,
        untrusted: true,
        data: { exitCode: out.exitCode, timedOut: out.timedOut, fullOutputPath },
      };
    },

    command_status: async (params) => {
      const jobId = String(params.job_id);
      const job = this.jobs.get(jobId);
      if (!job) throw new HelperRpcError('not_found', `Background job "${jobId}" not found.`);
      return {
        text: `Job ${job.id}: ${job.exited ? `exited (${job.exitCode})` : 'running'}\n${(job.stdout + '\n' + job.stderr).trim().slice(-8000)}`,
        untrusted: true,
        data: {
          job_id: job.id,
          exited: job.exited,
          exitCode: job.exitCode,
          outputFilePath: job.outputFilePath,
        },
      };
    },

    command_input: async (params) => {
      const jobId = String(params.job_id);
      const job = this.jobs.get(jobId);
      if (!job) throw new HelperRpcError('not_found', `Background job "${jobId}" not found.`);
      if (job.exited || !job.process.stdin) {
        throw new HelperRpcError('bad_request', `Background job "${jobId}" has already exited.`);
      }
      job.process.stdin.write(String(params.input), 'utf8');
      return { text: `Sent input to job ${jobId}.`, data: { job_id: jobId } };
    },

    command_kill: async (params) => {
      const jobId = String(params.job_id);
      const job = this.jobs.get(jobId);
      if (!job) throw new HelperRpcError('not_found', `Background job "${jobId}" not found.`);
      if (!job.exited && job.process.pid) {
        await this.runProcessCapture('taskkill.exe', ['/PID', String(job.process.pid), '/T', '/F'], 5000);
        job.exited = true;
      }
      return { text: `Terminated background job ${jobId}.`, data: { job_id: jobId, terminated: true } };
    },
  };

  private runProcessCapture(
    bin: string,
    args: string[],
    timeoutMs: number,
    cwd: string = process.cwd(),
    extraEnv: Record<string, string> = {},
  ): Promise<{ stdout: string; stderr: string; exitCode: number | null; timedOut: boolean }> {
    return new Promise((resolve, reject) => {
      let stdout = '';
      let stderr = '';
      let timedOut = false;
      let settled = false;

      let child: ChildProcess;
      try {
        child = spawn(bin, args, {
          cwd,
          env: { ...process.env, ...extraEnv },
          windowsHide: true,
          stdio: ['ignore', 'pipe', 'pipe'],
        });
      } catch (err) {
        reject(new HelperRpcError('failed', `Failed to start "${bin}": ${(err as Error).message}`));
        return;
      }

      const timer = setTimeout(() => {
        timedOut = true;
        if (child.pid) {
          spawn('taskkill.exe', ['/PID', String(child.pid), '/T', '/F'], { windowsHide: true });
        }
        if (!settled) {
          settled = true;
          reject(new HelperRpcError('timeout', `Command "${bin}" timed out after ${timeoutMs} ms.\n${stdout}\n${stderr}`));
        }
      }, timeoutMs);

      child.stdout?.on('data', (d: Buffer) => {
        stdout += d.toString('utf8');
      });
      child.stderr?.on('data', (d: Buffer) => {
        stderr += d.toString('utf8');
      });
      child.on('error', (err) => {
        if (!settled) {
          settled = true;
          clearTimeout(timer);
          reject(new HelperRpcError('failed', `Process "${bin}" error: ${err.message}`));
        }
      });
      child.on('close', (code) => {
        if (!settled) {
          settled = true;
          clearTimeout(timer);
          resolve({ stdout: stdout.trim(), stderr: stderr.trim(), exitCode: code, timedOut });
        }
      });
    });
  }
}
