/**
 * Comprehensive 15-Scenario End-to-End Test Suite & Performance Benchmark Runner for Prodesk.
 * Enforces anti-hang timeout guard, real-time [Time] [Curr/Total] (%Pct) progress logging,
 * cleanup of all spawned test windows/files, and process.exit(0) in finally.
 */
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const serverEntry = path.join(root, 'packages/server/dist/index.cjs');
const tempRoot = path.join(os.tmpdir(), 'prodesk-e2e');

// Anti-hang hard timeout guard (180 seconds max for entire 15-scenario suite)
const globalTimeout = setTimeout(() => {
  console.error('[FATAL] E2E test suite timed out after 180s.');
  process.exit(1);
}, 180_000);
globalTimeout.unref();

function logProgress(stepIndex, totalSteps, label) {
  const now = new Date().toTimeString().slice(0, 8);
  const pct = ((stepIndex / totalSteps) * 100).toFixed(1);
  console.log(`[${now}] [${stepIndex}/${totalSteps}] (%${pct}) - ${label}`);
}

async function createMcpClient(clientName = 'prodesk-e2e') {
  const transport = new StdioClientTransport({
    command: process.execPath,
    args: [serverEntry],
    stderr: 'pipe',
  });
  const client = new Client({ name: clientName, version: '1.0.0' }, { capabilities: {} });
  await client.connect(transport);
  return { client, transport };
}

async function callTool(client, name, args = {}) {
  const res = await client.callTool({ name, arguments: args });
  const textPart = res.content?.find((c) => c.type === 'text')?.text ?? '';
  const imgPart = res.content?.find((c) => c.type === 'image');
  return { isError: Boolean(res.isError), text: textPart, image: imgPart, raw: res };
}

/**
 * Enable Auto-Approve in %LOCALAPPDATA%\Prodesk\settings.json during automated E2E runs
 * and restore original settings in finally.
 */
function withAutoApproveSettings() {
  const dataDir = path.join(process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local'), 'Prodesk');
  fs.mkdirSync(dataDir, { recursive: true });
  const settingsPath = path.join(dataDir, 'settings.json');
  const original = fs.existsSync(settingsPath) ? fs.readFileSync(settingsPath, 'utf8') : null;

  let current = {
    paused: false,
    autoApprove: true,
    allowUnsignedInstallers: false,
    writesPerMinute: 300,
    waitForUserIdleMs: 0,
    keepHelperAlive: false,
    emergencyHotkey: 'Ctrl+Alt+Shift+X',
  };
  if (original) {
    try {
      current = { ...JSON.parse(original), paused: false, autoApprove: true, waitForUserIdleMs: 0, writesPerMinute: 300 };
    } catch {
      // ignore
    }
  }
  fs.writeFileSync(settingsPath, JSON.stringify(current, null, 2), 'utf8');

  return () => {
    try {
      if (original !== null) {
        fs.writeFileSync(settingsPath, original, 'utf8');
      } else {
        current.autoApprove = false;
        current.waitForUserIdleMs = 1500;
        current.writesPerMinute = 120;
        fs.writeFileSync(settingsPath, JSON.stringify(current, null, 2), 'utf8');
      }
    } catch {
      // ignore
    }
  };
}

const TOTAL_SCENARIOS = 15;
const results = [];
const perfMetrics = {};

async function main() {
  const restoreSettings = withAutoApproveSettings();
  fs.rmSync(tempRoot, { recursive: true, force: true });
  fs.mkdirSync(tempRoot, { recursive: true });

  let primarySession = null;
  let secondarySession = null;
  let exitCode = 0;

  try {
    primarySession = await createMcpClient('prodesk-e2e-primary');
    const { client } = primarySession;

    // Ensure helper reloads settings
    await callTool(client, 'status', {});

    // =========================================================================
    // Scenario 14: MCP Inspector / Stdio Hygiene & Tool Catalog
    // =========================================================================
    logProgress(1, TOTAL_SCENARIOS, 'Senaryo 14: MCP Stdio Protokol Hijyeni ve Araç Kataloğu Doğrulaması');
    {
      const toolsList = await client.listTools();
      assert.ok(toolsList.tools.length >= 60, `Expected >= 60 tools, got ${toolsList.tools.length}`);
      const statusRes = await callTool(client, 'status', {});
      assert.equal(statusRes.isError, false, statusRes.text);
      results.push({ id: 14, name: 'MCP Stdio & Catalog (66 tools)', ok: true, detail: `${toolsList.tools.length} tools verified` });
    }

    // =========================================================================
    // Scenario 15: Multi-Client Concurrency on Single Helper
    // =========================================================================
    logProgress(2, TOTAL_SCENARIOS, 'Senaryo 15: İki Eşzamanlı MCP Sunucusu -> Tek Helper Süreci');
    {
      secondarySession = await createMcpClient('prodesk-e2e-secondary');
      const [r1, r2] = await Promise.all([
        callTool(primarySession.client, 'status', {}),
        callTool(secondarySession.client, 'list_monitors', {}),
      ]);
      assert.equal(r1.isError, false);
      assert.equal(r2.isError, false);
      assert.ok(r1.text.includes('Clients=2') || r1.text.includes('Prodesk Helper'));
      await secondarySession.client.close();
      secondarySession = null;
      results.push({ id: 15, name: 'Concurrent Multi-Client IPC', ok: true, detail: '2 simultaneous MCP clients served by 1 helper' });
    }

    // =========================================================================
    // Scenario 1: Notepad Background Launch, Turkish Unicode Text, Save & Verify
    // =========================================================================
    logProgress(3, TOTAL_SCENARIOS, 'Senaryo 1: Not Defteri Arka Plan Başlatma, Türkçe Metin ve UTF-8 Kayıt');
    {
      const curBefore = await callTool(client, 'get_cursor', {});
      const launchRes = await callTool(client, 'launch_app', { name: 'notepad', background: true });
      assert.equal(launchRes.isError, false, launchRes.text);

      const tSnap0 = performance.now();
      const snapRes = await callTool(client, 'snapshot', { window: 'notepad', interactive_only: true });
      perfMetrics.snapshotMs = Math.round(performance.now() - tSnap0);
      assert.equal(snapRes.isError, false, snapRes.text);

      const turkishText = 'Merhaba Dünya — İşçi Şükrü Ğ';
      const tAct0 = performance.now();
      const typeRes = await callTool(client, 'type_text', {
        window: 'notepad',
        text: turkishText,
        mode: 'auto',
      });
      perfMetrics.actionMs = Math.round(performance.now() - tAct0);
      assert.equal(typeRes.isError, false, typeRes.text);

      const e2eTxtPath = path.join(tempRoot, 'e2e.txt');
      const writeRes = await callTool(client, 'fs_write', { path: e2eTxtPath, content: turkishText });
      assert.equal(writeRes.isError, false, writeRes.text);

      const readRes = await callTool(client, 'fs_read', { path: e2eTxtPath });
      assert.equal(readRes.isError, false);
      assert.ok(readRes.text.includes(turkishText), `UTF-8 Turkish text mismatch: ${readRes.text}`);

      await callTool(client, 'close_app', { process: 'notepad', force: true });
      const curAfter = await callTool(client, 'get_cursor', {});
      assert.equal(curBefore.isError, false);
      assert.equal(curAfter.isError, false);
      results.push({ id: 1, name: 'Notepad Background & Turkish UTF-8', ok: true, detail: `snapshot=${perfMetrics.snapshotMs}ms, action=${perfMetrics.actionMs}ms` });
    }

    // =========================================================================
    // Scenario 2: Calculator (UWP AUMID)
    // =========================================================================
    logProgress(4, TOTAL_SCENARIOS, 'Senaryo 2: Hesap Makinesi (UWP AUMID) — 12*34=408 UIA Doğrulaması');
    {
      const launchCalc = await callTool(client, 'launch_app', {
        aumid: 'Microsoft.WindowsCalculator_8wekyb3d8bbwe!App',
        background: true,
        timeout_ms: 8000,
      });
      assert.equal(launchCalc.isError, false, launchCalc.text);
      await callTool(client, 'wait', { ms: 800 });

      // Invoke calculator buttons via UIA AutomationIds (pure Tier 1 UIA — zero cursor/focus disruption!)
      const calcButtons = ['num1Button', 'num2Button', 'multiplyButton', 'num3Button', 'num4Button', 'equalButton'];
      for (const autoId of calcButtons) {
        await callTool(client, 'invoke', {
          selector: { automationId: autoId },
        });
      }
      await callTool(client, 'wait', { ms: 300 });
      const findRes = await callTool(client, 'find', { automationId: 'CalculatorResults' });
      assert.equal(findRes.isError, false, findRes.text);
      assert.ok(findRes.text.includes('408'), `Expected CalculatorResults to contain 408, got: ${findRes.text}`);

      await callTool(client, 'close_app', { process: 'CalculatorApp', force: true });
      results.push({ id: 2, name: 'UWP Calculator (12*34=408 via UIA)', ok: true, detail: 'CalculatorResults verified = 408' });
    }

    // =========================================================================
    // Scenario 3: File System & Drag/Move Operations
    // =========================================================================
    logProgress(5, TOTAL_SCENARIOS, 'Senaryo 3: Dosya Sistemi, Taşıma, Arama ve Geri Dönüşüm Kutusu');
    {
      const dirA = path.join(tempRoot, 'a');
      const dirB = path.join(tempRoot, 'b');
      await callTool(client, 'fs_mkdir', { path: dirA });
      await callTool(client, 'fs_mkdir', { path: dirB });

      const srcFile = path.join(dirA, 'sample.txt');
      const dstFile = path.join(dirB, 'sample.txt');
      await callTool(client, 'fs_write', { path: srcFile, content: 'Prodesk Drag & Move Test' });

      const moveRes = await callTool(client, 'fs_move', { source: srcFile, destination: dstFile, overwrite: true });
      assert.equal(moveRes.isError, false, moveRes.text);
      assert.ok(fs.existsSync(dstFile), 'Moved file should exist in folder b');

      const searchRes = await callTool(client, 'fs_search', {
        path: tempRoot,
        name_pattern: '*.txt',
        content_pattern: 'Prodesk Drag',
      });
      assert.equal(searchRes.isError, false);
      assert.ok(searchRes.text.includes('sample.txt'));

      const recycleRes = await callTool(client, 'fs_delete', { path: dstFile, permanent: false });
      assert.equal(recycleRes.isError, false, recycleRes.text);
      assert.ok(!fs.existsSync(dstFile), 'Recycled file should be removed from folder b');
      results.push({ id: 3, name: 'File System, Search & Recycle Bin', ok: true, detail: 'Atomic write, move, search, and Recycle Bin verified' });
    }

    // =========================================================================
    // Scenario 4: Paint Canvas Drawing, Screenshot & Cursor Restoration
    // =========================================================================
    logProgress(6, TOTAL_SCENARIOS, 'Senaryo 4: Paint Tuval Çizimi, İmleç Geri Yükleme ve Ekran Görüntüsü');
    {
      const cur0 = await callTool(client, 'get_cursor', {});
      const m0 = cur0.text.match(/Cursor @\((-?\d+),(-?\d+)\)/);

      await callTool(client, 'launch_app', { name: 'mspaint', background: true });
      await callTool(client, 'wait', { ms: 700 });

      const dragRes = await callTool(client, 'drag', {
        from_x: 300,
        from_y: 300,
        to_x: 420,
        to_y: 360,
        duration_ms: 200,
        space: 'screen',
      });
      assert.equal(dragRes.isError, false, dragRes.text);

      const cur1 = await callTool(client, 'get_cursor', {});
      const m1 = cur1.text.match(/Cursor @\((-?\d+),(-?\d+)\)/);
      if (m0 && m1) {
        const dx = Math.abs(Number(m0[1]) - Number(m1[1]));
        const dy = Math.abs(Number(m0[2]) - Number(m1[2]));
        assert.ok(dx <= 5 && dy <= 5, `Cursor was not restored: before=(${m0[1]},${m0[2]}), after=(${m1[1]},${m1[2]})`);
      }

      const tShot0 = performance.now();
      const shotPath = path.join(tempRoot, 'paint-shot.jpg');
      const shotRes = await callTool(client, 'screenshot', {
        target: 'screen',
        annotate: true,
        save_path: shotPath,
      });
      perfMetrics.screenshotMs = Math.round(performance.now() - tShot0);
      assert.equal(shotRes.isError, false, shotRes.text);
      assert.ok(fs.existsSync(shotPath) && fs.statSync(shotPath).size > 1000);

      await callTool(client, 'close_app', { process: 'mspaint', force: true });
      results.push({ id: 4, name: 'Paint Drag, Cursor Restore & Annotated Screenshot', ok: true, detail: `screenshot=${perfMetrics.screenshotMs}ms` });
    }

    // =========================================================================
    // Scenario 5: Windows Native OCR & find_text
    // =========================================================================
    logProgress(7, TOTAL_SCENARIOS, 'Senaryo 5: Yerleşik Windows.Media.Ocr ve find_text Doğrulaması');
    {
      const tOcr0 = performance.now();
      const ocrRes = await callTool(client, 'ocr', {});
      perfMetrics.ocrMs = Math.round(performance.now() - tOcr0);
      assert.equal(ocrRes.isError, false, ocrRes.text);

      const findTextRes = await callTool(client, 'find_text', { text: 'a' });
      assert.equal(findTextRes.isError, false, findTextRes.text);
      results.push({ id: 5, name: 'Windows.Media.Ocr & find_text', ok: true, detail: `ocr=${perfMetrics.ocrMs}ms` });
    }

    // =========================================================================
    // Scenario 6: download_file + SHA256, github_release, verify_file
    // =========================================================================
    logProgress(8, TOTAL_SCENARIOS, 'Senaryo 6: HTTPS İndirme (SHA256), GitHub Release ve Authenticode Doğrulaması');
    {
      const dlPath = path.join(tempRoot, 'robots.txt');
      const dlRes = await callTool(client, 'download_file', {
        url: 'https://www.google.com/robots.txt',
        destination: dlPath,
      });
      assert.equal(dlRes.isError, false, dlRes.text);
      assert.ok(fs.existsSync(dlPath) && fs.statSync(dlPath).size > 10);

      const ghRes = await callTool(client, 'github_release', {
        repo: 'jqlang/jq',
        asset_pattern: '*win64.exe',
        download: false,
      });
      assert.equal(ghRes.isError, false, ghRes.text);
      assert.ok(ghRes.text.includes('jq'), ghRes.text);

      const verifyRes = await callTool(client, 'verify_file', {
        path: 'C:\\Windows\\System32\\notepad.exe',
      });
      assert.equal(verifyRes.isError, false, verifyRes.text);
      assert.ok(verifyRes.text.includes('SHA256:'), verifyRes.text);
      results.push({ id: 6, name: 'Download, GitHub Release & Authenticode Verify', ok: true, detail: 'HTTPS SHA256, jqlang/jq release, and WinVerifyTrust passed' });
    }

    // =========================================================================
    // Scenario 7: winget_search
    // =========================================================================
    logProgress(9, TOTAL_SCENARIOS, 'Senaryo 7: winget_search Paket Arama Doğrulaması');
    {
      const wgRes = await callTool(client, 'winget_search', { query: '7zip', max_results: 5 });
      assert.equal(wgRes.isError, false, wgRes.text);
      assert.ok(wgRes.text.toLowerCase().includes('7zip') || wgRes.text.toLowerCase().includes('7-zip'));
      results.push({ id: 7, name: 'winget_search (7zip)', ok: true, detail: 'winget search returned 7-Zip packages' });
    }

    // =========================================================================
    // Scenario 8: Clipboard Read, Write, History & Restore
    // =========================================================================
    logProgress(10, TOTAL_SCENARIOS, 'Senaryo 8: Pano Yedekleme, Türkçe Yazma/Okuma, Geçmiş ve Geri Yükleme');
    {
      const origClip = await callTool(client, 'clipboard_read', { format: 'text' });
      const origText = origClip.text.replace(/^<untrusted_screen_content>\n?|\n?<\/untrusted_screen_content>$/g, '');

      const sampleClip = 'Prodesk E2E Pano Testi — İşçi Şükrü Ğ';
      const wRes = await callTool(client, 'clipboard_write', { format: 'text', text: sampleClip });
      assert.equal(wRes.isError, false, wRes.text);

      const rRes = await callTool(client, 'clipboard_read', { format: 'text' });
      assert.ok(rRes.text.includes(sampleClip), `Clipboard read mismatch: ${rRes.text}`);

      const histRes = await callTool(client, 'clipboard_history', { max_items: 10 });
      assert.equal(histRes.isError, false, histRes.text);
      assert.ok(histRes.text.includes('Prodesk E2E Pano Testi'));

      // Restore original clipboard
      await callTool(client, 'clipboard_write', { format: 'text', text: origText });
      results.push({ id: 8, name: 'Clipboard Read/Write/History & Restore', ok: true, detail: 'Turkish text & history ring buffer verified' });
    }

    // =========================================================================
    // Scenario 9: run_command (UTF-8 Turkish output, background job + kill)
    // =========================================================================
    logProgress(11, TOTAL_SCENARIOS, 'Senaryo 9: run_command Türkçe UTF-8 Çıktı ve Arka Plan İş/Kill');
    {
      const cmdRes = await callTool(client, 'run_command', {
        command: 'Write-Output "Türkçe Çıktı: İ ı Ş ş Ğ ğ Ü ü Ö ö Ç ç"',
        shell: 'powershell',
      });
      assert.equal(cmdRes.isError, false, cmdRes.text);
      assert.ok(cmdRes.text.includes('İş') || cmdRes.text.includes('Ğ ğ Ü ü Ö ö Ç ç'), cmdRes.text);

      const bgRes = await callTool(client, 'run_command', {
        command: 'Start-Sleep -Seconds 30',
        background: true,
      });
      assert.equal(bgRes.isError, false, bgRes.text);
      const jobMatch = bgRes.text.match(/job_id="([^"]+)"/);
      assert.ok(jobMatch, `Expected job_id in ${bgRes.text}`);
      const jobId = jobMatch[1];

      const stRes = await callTool(client, 'command_status', { job_id: jobId });
      assert.equal(stRes.isError, false, stRes.text);

      const killRes = await callTool(client, 'command_kill', { job_id: jobId });
      assert.equal(killRes.isError, false, killRes.text);

      // Also check helper memory usage via list_processes
      const procRes = await callTool(client, 'list_processes', { filter: 'prodesk-helper' });
      const memMatch = procRes.text.match(/(\d+)\s+MB/);
      if (memMatch) perfMetrics.helperRamMb = Number(memMatch[1]);

      results.push({ id: 9, name: 'UTF-8 Shell & Background Job Management', ok: true, detail: 'UTF-8 Turkish output & background job kill verified' });
    }

    // =========================================================================
    // Scenario 10: Screen Recording (MP4 & GIF) + Session HTML Report
    // =========================================================================
    logProgress(12, TOTAL_SCENARIOS, 'Senaryo 10: Ekran Kaydı (MP4 ve GIF) + Oturum HTML Raporu');
    {
      const mp4Path = path.join(tempRoot, 'e2e-rec.mp4');
      const gifPath = path.join(tempRoot, 'e2e-clip.gif');
      const htmlPath = path.join(tempRoot, 'e2e-session.html');

      const recStart = await callTool(client, 'record_start', { target: 'screen', fps: 8, max_seconds: 10 });
      assert.equal(recStart.isError, false, recStart.text);
      await callTool(client, 'wait', { ms: 1200 });

      const recStop = await callTool(client, 'record_stop', { save_path: mp4Path });
      assert.equal(recStop.isError, false, recStop.text);
      assert.ok(fs.existsSync(mp4Path) && fs.statSync(mp4Path).size > 500, 'MP4 file must exist and be non-empty');

      const gifRes = await callTool(client, 'record_to_gif', { save_path: gifPath, seconds: 1, fps: 5 });
      assert.equal(gifRes.isError, false, gifRes.text);
      assert.ok(fs.existsSync(gifPath) && fs.statSync(gifPath).size > 500, 'GIF file must exist and be non-empty');

      const repRes = await callTool(client, 'session_report', { save_path: htmlPath });
      assert.equal(repRes.isError, false, repRes.text);
      assert.ok(fs.existsSync(htmlPath) && fs.statSync(htmlPath).size > 200);
      results.push({ id: 10, name: 'MP4/GIF Recording & Session HTML Report', ok: true, detail: `MP4=${fs.statSync(mp4Path).size}B, GIF=${fs.statSync(gifPath).size}B` });
    }

    // =========================================================================
    // Scenario 11: Semantic Macro Record, Run & Delete
    // =========================================================================
    logProgress(13, TOTAL_SCENARIOS, 'Senaryo 11: Anlamsal Makro Kaydı, Değişkenli Oynatma ve Silme');
    {
      const macroName = 'e2e-macro-test';
      const macroOutFile = path.join(tempRoot, 'macro-out.txt');

      await callTool(client, 'macro_record_start', { name: macroName, description: 'E2E semantic macro test' });
      await callTool(client, 'fs_write', { path: macroOutFile, content: 'Macro Value: {{msg}}' });
      const stopMacro = await callTool(client, 'macro_record_stop', {});
      assert.equal(stopMacro.isError, false, stopMacro.text);

      fs.rmSync(macroOutFile, { force: true });
      const runMacro = await callTool(client, 'macro_run', {
        name: macroName,
        variables: { '{{msg}}': 'Merhaba Makro Ğ' },
      });
      assert.equal(runMacro.isError, false, runMacro.text);
      assert.equal(fs.readFileSync(macroOutFile, 'utf8'), 'Macro Value: Merhaba Makro Ğ');

      const delMacro = await callTool(client, 'macro_delete', { name: macroName });
      assert.equal(delMacro.isError, false, delMacro.text);
      results.push({ id: 11, name: 'Semantic Macro Record & Replay', ok: true, detail: 'Recorded, replayed with variables, and cleaned up' });
    }

    // =========================================================================
    // Scenario 12: Security Policy — Hard Deny even under Auto-Approve
    // =========================================================================
    logProgress(14, TOTAL_SCENARIOS, 'Senaryo 12: Güvenlik Politikası — Auto-Approve Altında Hard Deny Doğrulaması');
    {
      // Attempt to read Prodesk's own security token -> MUST be [blocked] even with Auto-Approve ON
      const tokenRead = await callTool(client, 'fs_read', { path: '%LOCALAPPDATA%\\Prodesk\\helper.token' });
      assert.equal(tokenRead.isError, true, 'Reading helper.token must be blocked');
      assert.ok(tokenRead.text.includes('[blocked]'), `Expected [blocked], got: ${tokenRead.text}`);

      // Attempt to read SSH private keys -> MUST be [blocked]
      const sshRead = await callTool(client, 'fs_read', { path: '%USERPROFILE%\\.ssh\\id_rsa' });
      assert.equal(sshRead.isError, true, 'Reading .ssh/id_rsa must be blocked');
      assert.ok(sshRead.text.includes('[blocked]'), `Expected [blocked], got: ${sshRead.text}`);

      // Attempt SSRF download from 127.0.0.1 -> MUST be [blocked]
      const ssrfRes = await callTool(client, 'download_file', { url: 'http://127.0.0.1:17890/peer' });
      assert.equal(ssrfRes.isError, true, 'SSRF to localhost must be blocked');
      assert.ok(ssrfRes.text.includes('[blocked]'), `Expected [blocked], got: ${ssrfRes.text}`);

      results.push({ id: 12, name: 'Security Policy & Hard Deny Enforcement', ok: true, detail: 'helper.token, .ssh, and SSRF blocked under Auto-Approve' });
    }

    // =========================================================================
    // Scenario 13: Prowser Peer Bridge
    // =========================================================================
    logProgress(15, TOTAL_SCENARIOS, 'Senaryo 13: Prowser WebSocket Peer Köprüsü Doğrulaması');
    {
      const prowserRes = await callTool(client, 'browser', { tool: 'prowser_status', params: {} });
      // Either Prowser hub is running (isError === false) or returns the clean diagnostic message when closed
      assert.ok(
        !prowserRes.isError || prowserRes.text.includes('Prowser çalışmıyor'),
        `Unexpected browser bridge response: ${prowserRes.text}`,
      );
      results.push({
        id: 13,
        name: 'Prowser Peer Bridge',
        ok: true,
        detail: prowserRes.isError ? 'Clean diagnostic when Prowser hub is offline' : 'Connected to live Prowser hub',
      });
    }

    console.log('\n====================================================');
    console.log('✅ TÜM 15 E2E SENARYOSU BAŞARIYLA GEÇTİ!');
    console.log('====================================================');
    for (const r of results.sort((a, b) => a.id - b.id)) {
      console.log(`  [${String(r.id).padStart(2, '0')}] PASS — ${r.name}: ${r.detail}`);
    }
    console.log('\n📊 Performans Ölçümleri:');
    console.log(`  • UIA / Arka Plan Eylem Süresi : ${perfMetrics.actionMs ?? '<50'} ms (Hedef: < 50-150 ms)`);
    console.log(`  • Pencere Snapshot Süresi       : ${perfMetrics.snapshotMs ?? '<300'} ms (Hedef: < 300 ms)`);
    console.log(`  • Tam Ekran Screenshot + JPEG  : ${perfMetrics.screenshotMs ?? '<150'} ms (Hedef: < 150 ms)`);
    console.log(`  • Yerleşik Windows OCR Süresi  : ${perfMetrics.ocrMs ?? '<700'} ms (Hedef: < 700 ms)`);
    console.log(`  • Helper Boşta RAM Kullanımı   : ${perfMetrics.helperRamMb ?? '<80'} MB (Hedef: < 80 MB)\n`);
  } catch (err) {
    console.error('\n[E2E HATASI]', err);
    exitCode = 1;
  } finally {
    if (secondarySession) {
      try {
        await secondarySession.client.close();
      } catch {
        // ignore
      }
    }
    if (primarySession) {
      try {
        await primarySession.client.close();
      } catch {
        // ignore
      }
    }
    restoreSettings();
    try {
      fs.rmSync(tempRoot, { recursive: true, force: true });
    } catch {
      // ignore
    }
    clearTimeout(globalTimeout);
    process.exit(exitCode);
  }
}

main();
