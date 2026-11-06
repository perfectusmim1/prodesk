/**
 * One-click idempotent installer for Prodesk:
 * 1. Verifies Node >= 20 and builds if needed.
 * 2. Copies index.cjs and prodesk-helper.exe to %LOCALAPPDATA%\Prodesk\app\.
 * 3. Creates %LOCALAPPDATA%\Prodesk\helper.token if missing.
 * 4. Installs Antigravity plugin to ~/.gemini/config/plugins/prodesk/ (with mcp_config.json)
 *    and removes any duplicate global "prodesk" entry from ~/.gemini/config/mcp_config.json.
 * 5. Configures OpenCode (opencode.json / opencode.jsonc) preserving comments via jsonc-parser.
 * 6. Configures Claude Desktop (claude_desktop_config.json) if present.
 * 7. Adds/updates Section 8 "Bilgisayar Kontrolü (Prodesk MCP)" in ~/.gemini/GEMINI.md.
 */
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import crypto from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { applyEdits, modify } from 'jsonc-parser';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const distServer = path.join(root, 'packages/server/dist/index.cjs');
const distHelper = path.join(root, 'packages/server/dist/prodesk-helper.exe');

console.log('====================================================');
console.log('⚡ Prodesk Kurulum & Yapılandırma (Windows Computer Use)');
console.log('====================================================\n');

const nodeMajor = Number(process.versions.node.split('.')[0]);
if (nodeMajor < 20) {
  console.error(`[HATA] Node.js >= 20 gereklidir (mevcut: ${process.version}).`);
  process.exit(1);
}

if (!fs.existsSync(distServer) || !fs.existsSync(distHelper)) {
  console.log('[INFO] Derleme çıktıları eksik, önce "npm run build" çalıştırılıyor...');
  execFileSync(process.execPath, [path.join(root, 'scripts/build.mjs')], { stdio: 'inherit' });
}

// Stop running helper before updating %LOCALAPPDATA%\Prodesk\app\prodesk-helper.exe
try {
  execFileSync('taskkill.exe', ['/IM', 'prodesk-helper.exe', '/F'], { stdio: 'ignore' });
} catch {
  // not running
}

const home = os.homedir();
const localAppData = process.env.LOCALAPPDATA || path.join(home, 'AppData', 'Local');
const dataDir = path.join(localAppData, 'Prodesk');
const appDir = path.join(dataDir, 'app');
fs.mkdirSync(appDir, { recursive: true });

const installedServer = path.join(appDir, 'index.cjs');
const installedHelper = path.join(appDir, 'prodesk-helper.exe');
fs.copyFileSync(distServer, installedServer);
fs.copyFileSync(distHelper, installedHelper);
console.log(`[OK] Uygulama dosyaları kopyalandı: ${appDir}`);

// Ensure helper.token exists
const tokenPath = path.join(dataDir, 'helper.token');
if (!fs.existsSync(tokenPath) || fs.readFileSync(tokenPath, 'utf8').trim().length < 32) {
  const token = crypto.randomBytes(32).toString('hex');
  fs.writeFileSync(tokenPath, token, { mode: 0o600 });
  console.log(`[OK] Güvenlik token'ı oluşturuldu: ${tokenPath}`);
} else {
  console.log(`[OK] Güvenlik token'ı mevcut: ${tokenPath}`);
}

const nowStr = new Date().toISOString().replace(/[:.]/g, '-');

// Install Antigravity plugin to ~/.gemini/config/plugins/prodesk
const pluginSrcDir = path.join(root, 'plugin');
const pluginDestDir = path.join(home, '.gemini', 'config', 'plugins', 'prodesk');
fs.mkdirSync(pluginDestDir, { recursive: true });
fs.cpSync(pluginSrcDir, pluginDestDir, { recursive: true, force: true });

const pluginMcpConfig = {
  mcpServers: {
    prodesk: {
      command: 'node',
      args: [installedServer],
    },
  },
};
fs.writeFileSync(
  path.join(pluginDestDir, 'mcp_config.json'),
  JSON.stringify(pluginMcpConfig, null, 2),
  'utf8',
);
console.log(`[OK] Antigravity plugin paketi kuruldu: ${pluginDestDir}`);

// Also copy skill to ~/.gemini/config/skills/prodesk
const skillDestDir = path.join(home, '.gemini', 'config', 'skills', 'prodesk');
fs.mkdirSync(skillDestDir, { recursive: true });
fs.copyFileSync(
  path.join(pluginSrcDir, 'skills', 'prodesk', 'SKILL.md'),
  path.join(skillDestDir, 'SKILL.md'),
);
console.log(`[OK] Antigravity skill kopyalandı: ${skillDestDir}`);

// Remove duplicate "prodesk" entry from global mcp_config.json if previously added (§9.3)
const globalMcpPath = path.join(home, '.gemini', 'config', 'mcp_config.json');
if (fs.existsSync(globalMcpPath)) {
  try {
    const raw = fs.readFileSync(globalMcpPath, 'utf8').trim();
    if (raw) {
      const parsed = JSON.parse(raw);
      if (parsed?.mcpServers?.prodesk) {
        const bak = `${globalMcpPath}.bak-prodesk-${nowStr}`;
        fs.copyFileSync(globalMcpPath, bak);
        delete parsed.mcpServers.prodesk;
        fs.writeFileSync(globalMcpPath, JSON.stringify(parsed, null, 2), 'utf8');
        console.log(`[OK] Çakışmayı önlemek için global mcp_config.json içinden mükerrer prodesk kaydı kaldırıldı.`);
      }
    }
  } catch (e) {
    console.warn(`[WARN] Global mcp_config.json kontrol edilemedi: ${e.message}`);
  }
}

// Configure OpenCode: ~/.config/opencode/opencode.json and opencode.jsonc
const opencodeDir = path.join(home, '.config', 'opencode');
if (fs.existsSync(opencodeDir)) {
  for (const filename of ['opencode.json', 'opencode.jsonc']) {
    const filePath = path.join(opencodeDir, filename);
    if (!fs.existsSync(filePath)) continue;
    try {
      const backupPath = `${filePath}.bak-prodesk-${nowStr}`;
      fs.copyFileSync(filePath, backupPath);
      const original = fs.readFileSync(filePath, 'utf8');
      const prodeskEntry = {
        type: 'local',
        command: ['node', installedServer],
        enabled: true,
      };
      const edits = modify(original, ['mcp', 'prodesk'], prodeskEntry, {
        formattingOptions: { insertSpaces: true, tabSize: 2 },
      });
      const updated = applyEdits(original, edits);
      fs.writeFileSync(filePath, updated, 'utf8');
      console.log(`[OK] OpenCode yapılandırıldı: ${filePath}`);
    } catch (e) {
      console.warn(`[WARN] OpenCode yapılandırılamadı (${filePath}): ${e.message}`);
    }
  }
}

// Configure Claude Desktop if config exists
const appDataRoaming = process.env.APPDATA || path.join(home, 'AppData', 'Roaming');
const claudeConfigPath = path.join(appDataRoaming, 'Claude', 'claude_desktop_config.json');
if (fs.existsSync(claudeConfigPath)) {
  try {
    const backupPath = `${claudeConfigPath}.bak-prodesk-${nowStr}`;
    fs.copyFileSync(claudeConfigPath, backupPath);
    const raw = fs.readFileSync(claudeConfigPath, 'utf8').trim();
    const parsed = raw ? JSON.parse(raw) : {};
    parsed.mcpServers = parsed.mcpServers || {};
    parsed.mcpServers.prodesk = {
      command: 'node',
      args: [installedServer],
    };
    fs.writeFileSync(claudeConfigPath, JSON.stringify(parsed, null, 2), 'utf8');
    console.log(`[OK] Claude Desktop yapılandırıldı: ${claudeConfigPath}`);
  } catch (e) {
    console.warn(`[WARN] Claude Desktop yapılandırılamadı: ${e.message}`);
  }
}

// Update ~/.gemini/GEMINI.md with Section 8 "Bilgisayar Kontrolü (Prodesk MCP)"
const geminiMdPath = path.join(home, '.gemini', 'GEMINI.md');
const prodeskSection = `## 8. Bilgisayar Kontrolü & Masaüstü Otomasyonu (Prodesk MCP)
- **Kullanıcıyı Bölmeme (Arka Plan Önceliği):** Masaüstü uygulamalarıyla çalışırken kullanıcının imlecini ve aktif pencere odağını çalmamak birincil kuraldır. Uygulamalar her zaman \`launch_app({ background: true })\` ile açılır; etkileşimlerde varsayılan \`mode: "auto"\` (UIA -> Win32 Mesajı -> SendInput + odak/imleç geri yükleme) kullanılır.
- **Önce Yapı (\`snapshot\` + \`uid\`), Sonra Piksel:** Koordinatla körlemesine tıklamak yerine önce \`snapshot\` ile öğelerin \`uid\` (\`[w1:e5]\`) değerleri alınır ve eylemlerde \`uid\` / \`selector\` kullanılır. Çok adımlı masaüstü işleri \`batch\` ile tek çağrıda birleştirilir.
- **Web İçeriği = Prowser:** Web sayfalarının DOM içeriği için ASLA masaüstü OCR veya koordinat tıklaması kullanılmaz; web işleri doğrudan \`prowser\` MCP'ye (\`new_window\` ile) devredilir. Prodesk tarayıcıda yalnızca yerel işletim sistemi diyalogları ("Dosya Aç", "Farklı Kaydet", "Yazdır") veya pencere yönetimi için kullanılır.
- **Temizlik:** Görev tamamlandığında açılan geçici masaüstü uygulamaları ve pencereler kapatılır.
`;

if (fs.existsSync(geminiMdPath)) {
  try {
    const backupPath = `${geminiMdPath}.bak-prodesk-${nowStr}`;
    fs.copyFileSync(geminiMdPath, backupPath);
    let md = fs.readFileSync(geminiMdPath, 'utf8');
    const sec8Regex = /## 8\. Bilgisayar Kontrolü[\s\S]*?(?=\n## \d+\.|\s*$)/;
    if (sec8Regex.test(md)) {
      md = md.replace(sec8Regex, prodeskSection.trimEnd());
    } else {
      md = md.trimEnd() + '\n\n---\n\n' + prodeskSection;
    }
    fs.writeFileSync(geminiMdPath, md, 'utf8');
    console.log(`[OK] ~/.gemini/GEMINI.md Bölüm 8 güncellendi.`);
  } catch (e) {
    console.warn(`[WARN] GEMINI.md güncellenemedi: ${e.message}`);
  }
}

console.log('\n====================================================');
console.log('🎉 Prodesk Kurulumu Tamamlandı!');
console.log('====================================================');
console.log('• Plugin dizini : ' + pluginDestDir);
console.log('• Çalışma dizini: ' + appDir);
console.log('• Sonraki adım  : Antigravity IDE\'yi yeniden başlatın.');
