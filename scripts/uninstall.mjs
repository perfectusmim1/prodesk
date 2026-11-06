/**
 * Cleanly uninstalls Prodesk from Antigravity, OpenCode, Claude Desktop, GEMINI.md, and %LOCALAPPDATA%\Prodesk\app.
 */
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { execFileSync } from 'node:child_process';
import { applyEdits, modify } from 'jsonc-parser';

console.log('====================================================');
console.log('🧹 Prodesk Kaldırma (Uninstall)');
console.log('====================================================\n');

const home = os.homedir();
const localAppData = process.env.LOCALAPPDATA || path.join(home, 'AppData', 'Local');
const dataDir = path.join(localAppData, 'Prodesk');

// 1. Stop running helper
try {
  execFileSync('taskkill.exe', ['/IM', 'prodesk-helper.exe', '/F'], { stdio: 'ignore' });
  console.log('[OK] Çalışan prodesk-helper.exe sonlandırıldı.');
} catch {
  // not running
}

// 2. Remove Antigravity plugin & skill
const pluginDir = path.join(home, '.gemini', 'config', 'plugins', 'prodesk');
if (fs.existsSync(pluginDir)) {
  fs.rmSync(pluginDir, { recursive: true, force: true });
  console.log(`[OK] Kaldırıldı: ${pluginDir}`);
}
const skillDir = path.join(home, '.gemini', 'config', 'skills', 'prodesk');
if (fs.existsSync(skillDir)) {
  fs.rmSync(skillDir, { recursive: true, force: true });
  console.log(`[OK] Kaldırıldı: ${skillDir}`);
}

// 3. Remove from OpenCode configs
const opencodeDir = path.join(home, '.config', 'opencode');
if (fs.existsSync(opencodeDir)) {
  for (const filename of ['opencode.json', 'opencode.jsonc']) {
    const filePath = path.join(opencodeDir, filename);
    if (!fs.existsSync(filePath)) continue;
    try {
      const original = fs.readFileSync(filePath, 'utf8');
      const edits = modify(original, ['mcp', 'prodesk'], undefined, {
        formattingOptions: { insertSpaces: true, tabSize: 2 },
      });
      fs.writeFileSync(filePath, applyEdits(original, edits), 'utf8');
      console.log(`[OK] OpenCode kaydı silindi: ${filePath}`);
    } catch {
      // ignore
    }
  }
}

// 4. Remove from Claude Desktop config
const appDataRoaming = process.env.APPDATA || path.join(home, 'AppData', 'Roaming');
const claudeConfigPath = path.join(appDataRoaming, 'Claude', 'claude_desktop_config.json');
if (fs.existsSync(claudeConfigPath)) {
  try {
    const parsed = JSON.parse(fs.readFileSync(claudeConfigPath, 'utf8'));
    if (parsed?.mcpServers?.prodesk) {
      delete parsed.mcpServers.prodesk;
      fs.writeFileSync(claudeConfigPath, JSON.stringify(parsed, null, 2), 'utf8');
      console.log(`[OK] Claude Desktop kaydı silindi: ${claudeConfigPath}`);
    }
  } catch {
    // ignore
  }
}

// 5. Remove Section 8 from ~/.gemini/GEMINI.md
const geminiMdPath = path.join(home, '.gemini', 'GEMINI.md');
if (fs.existsSync(geminiMdPath)) {
  try {
    let md = fs.readFileSync(geminiMdPath, 'utf8');
    const cleaned = md.replace(/\n*---\n+## 8\. Bilgisayar Kontrolü[\s\S]*?(?=\n## \d+\.|\s*$)/, '\n');
    if (cleaned !== md) {
      fs.writeFileSync(geminiMdPath, cleaned, 'utf8');
      console.log('[OK] ~/.gemini/GEMINI.md Bölüm 8 kaldırıldı.');
    }
  } catch {
    // ignore
  }
}

// 6. Remove %LOCALAPPDATA%\Prodesk\app (and full dataDir if --purge is passed)
const appDir = path.join(dataDir, 'app');
if (fs.existsSync(appDir)) {
  fs.rmSync(appDir, { recursive: true, force: true });
  console.log(`[OK] Uygulama klasörü silindi: ${appDir}`);
}

if (process.argv.includes('--purge') && fs.existsSync(dataDir)) {
  fs.rmSync(dataDir, { recursive: true, force: true });
  console.log(`[OK] Tüm Prodesk verileri (audit/makrolar dahil) silindi: ${dataDir}`);
} else {
  console.log(`[INFO] Audit ve makro kayıtları korundu (${dataDir}). Tamamen silmek için "--purge" ekleyin.`);
}

console.log('\n[OK] Prodesk başarıyla kaldırıldı.');
