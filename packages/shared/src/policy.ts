import path from 'node:path';
import os from 'node:os';

export type Risk = 'read' | 'write' | 'destructive' | 'critical';

/** Base risk classification per tool. Unknown tools default to critical. */
export const TOOL_RISK: Record<string, Risk> = {
  // 5.1 System
  status: 'read',
  list_monitors: 'read',
  get_cursor: 'read',
  wait: 'read',
  batch: 'read', // each inner step is evaluated individually
  computer: 'write', // screenshot/cursor_position/wait downgraded dynamically

  // 5.2 Observation
  screenshot: 'read',
  snapshot: 'read',
  find: 'read',
  element_at: 'read',
  get_element: 'read',
  ocr: 'read',
  find_text: 'read',
  wait_for: 'read',

  // 5.3 Input & Interaction
  click: 'write',
  hover: 'write',
  mouse_move: 'write',
  drag: 'write',
  scroll: 'write',
  type_text: 'write', // escalated to critical if target is IsPassword=true
  press_key: 'write',
  set_value: 'write', // escalated to critical if target is IsPassword=true
  invoke: 'write',
  toggle: 'write',
  select_option: 'write',
  expand: 'write',
  collapse: 'write',
  focus: 'write',

  // 5.4 Window & App Management
  list_windows: 'read',
  focus_window: 'write',
  window_action: 'write', // close on non-AI window -> destructive
  launch_app: 'write',
  close_app: 'destructive',
  list_apps: 'read',
  open_path: 'write',

  // 5.5 File System
  fs_list: 'read',
  fs_info: 'read',
  fs_read: 'read',
  fs_write: 'write',
  fs_copy: 'write',
  fs_move: 'destructive',
  fs_mkdir: 'write',
  fs_delete: 'destructive', // permanent: true -> critical
  fs_search: 'read',
  reveal_in_explorer: 'read',

  // 5.6 Downloads & Package Installation
  download_file: 'destructive',
  github_release: 'destructive',
  winget_search: 'read',
  winget_list: 'read',
  winget_install: 'destructive',
  winget_uninstall: 'destructive',
  verify_file: 'read',
  run_installer: 'destructive', // unsigned -> critical

  // 5.7 Clipboard
  clipboard_read: 'read',
  clipboard_write: 'write',
  clipboard_history: 'destructive',
  clipboard_clear: 'write',

  // 5.8 Shell & Processes
  run_command: 'destructive', // dangerous patterns -> critical
  command_status: 'read',
  command_input: 'write',
  command_kill: 'write',
  list_processes: 'read',
  kill_process: 'destructive',
  system_info: 'read',
  notify: 'write',

  // 5.9 Screen Recording & Macros
  record_start: 'write',
  record_stop: 'read',
  record_to_gif: 'write',
  session_report: 'read',
  macro_record_start: 'write',
  macro_record_stop: 'read',
  macro_run: 'write',
  macro_list: 'read',
  macro_delete: 'destructive',

  // 5.10 Prowser Integration
  browser: 'write',
};

const RANK: Record<Risk, number> = { read: 0, write: 1, destructive: 2, critical: 3 };
export const maxRisk = (a: Risk, b: Risk): Risk => (RANK[a] >= RANK[b] ? a : b);
export const isWriteOrAbove = (r: Risk): boolean => RANK[r] >= 1;

// ---------------------------------------------------------------------------
// Dangerous Command Static Scanner (§7.2)
// ---------------------------------------------------------------------------
const DANGEROUS_COMMAND_PATTERNS: Array<[RegExp, string]> = [
  [/\bRemove-Item\b(?=.*(?:\s-(?:Recurse|r|Force|f)\b))/i, 'recursive or forced Remove-Item'],
  [/\b(?:rm|del|erase)\b\s+(?:-[rRfF]{1,4}|\/[sSqQfF])\b/i, 'recursive/forced file deletion'],
  [/\b(?:rd|rmdir)\b\s+\/[sSqQ]/i, 'recursive directory deletion'],
  [/\bformat(?:\.com|\.exe)?\s+[a-zA-Z]:/i, 'formats disk volume'],
  [/\bdiskpart\b/i, 'invokes diskpart partition utility'],
  [/\bbcdedit\b/i, 'modifies boot configuration data (bcdedit)'],
  [/\breg(?:\.exe)?\s+(?:delete|add|import|restore|load)\b/i, 'modifies Windows Registry via reg.exe'],
  [/\b(?:Set|New|Remove|Clear)-ItemProperty\b.*\bHKLM:/i, 'modifies HKLM registry hive'],
  [/\bSet-ExecutionPolicy\b/i, 'changes PowerShell execution policy'],
  [/\b(?:iwr|Invoke-WebRequest|irm|Invoke-RestMethod|curl|wget)\b[\s\S]*\|\s*(?:iex|Invoke-Expression)\b/i, 'pipes remote script into Invoke-Expression'],
  [/\bDownloadString\s*\(|\bDownloadFile\s*\(/i, 'downloads remote payload via WebClient'],
  [/\b(?:iex|Invoke-Expression)\b/i, 'executes dynamic PowerShell expression'],
  [/\b(?:shutdown(?:\.exe)?|Stop-Computer|Restart-Computer)\b/i, 'shuts down or restarts computer'],
  [/\bcipher(?:\.exe)?\s+\/w/i, 'wipes disk free space'],
  [/\bvssadmin\b[\s\S]*\bdelete\b/i, 'deletes Volume Shadow Copies'],
  [/\bwmic\b[\s\S]*\bshadowcopy\b/i, 'manipulates shadow copies via wmic'],
  [/\b(?:netsh\b[\s\S]*\badvfirewall\b|Set-NetFirewallProfile\b)/i, 'modifies Windows Firewall configuration'],
  [/\b(?:Add|Set)-MpPreference\b|-DisableRealtimeMonitoring\b|-ExclusionPath\b/i, 'modifies Windows Defender exclusions or protection'],
  [/\btakeown(?:\.exe)?\b/i, 'takes ownership of system files'],
  [/\bicacls(?:\.exe)?\b[\s\S]*\/(?:grant|setowner|remove)/i, 'modifies file ACL permissions'],
  [/\s-(?:EncodedCommand|enc|e)\s+[A-Za-z0-9+/=]{8,}/i, 'executes obfuscated Base64 encoded command'],
  [/\b(?:schtasks(?:\.exe)?\s+\/create|Register-ScheduledTask)\b/i, 'creates persistent scheduled task'],
  [/\bnet(?:\.exe)?\s+(?:user|localgroup\s+administrators)\b/i, 'modifies local user accounts or administrators'],
];

export function scanCommand(command: string): string[] {
  if (!command) return [];
  const hits: string[] = [];
  for (const [re, label] of DANGEROUS_COMMAND_PATTERNS) {
    if (re.test(command)) hits.push(label);
  }
  return hits;
}

// ---------------------------------------------------------------------------
// Blocked Applications (§7.3)
// ---------------------------------------------------------------------------
export interface AppRule {
  pattern: string; // process name or window title pattern
  mode: 'block' | 'readonly';
  reason: string;
}

export const DEFAULT_APP_RULES: AppRule[] = [
  { pattern: '1password*', mode: 'block', reason: 'Password manager (1Password)' },
  { pattern: 'bitwarden*', mode: 'block', reason: 'Password manager (Bitwarden)' },
  { pattern: 'keepass*', mode: 'block', reason: 'Password manager (KeePass/KeePassXC)' },
  { pattern: 'lastpass*', mode: 'block', reason: 'Password manager (LastPass)' },
  { pattern: 'dashlane*', mode: 'block', reason: 'Password manager (Dashlane)' },
  { pattern: 'protonpass*', mode: 'block', reason: 'Password manager (Proton Pass)' },
  { pattern: 'nordpass*', mode: 'block', reason: 'Password manager (NordPass)' },
  { pattern: 'credentialuibroker*', mode: 'block', reason: 'Windows Credential UI Broker' },
  { pattern: 'consent.exe', mode: 'block', reason: 'UAC Elevation Prompt' },
  { pattern: 'sechealthui*', mode: 'block', reason: 'Windows Security UI' },
  { pattern: 'lsass.exe', mode: 'block', reason: 'Local Security Authority Subsystem' },
  { pattern: 'winlogon.exe', mode: 'block', reason: 'Windows Logon Process' },
  { pattern: 'csrss.exe', mode: 'block', reason: 'Client Server Runtime Process' },
  { pattern: 'mmc.exe', mode: 'block', reason: 'Microsoft Management Console' },
  { pattern: 'regedit*', mode: 'readonly', reason: 'Registry Editor (read-only)' },
];

const globToRegExp = (g: string) =>
  new RegExp('^' + g.split('*').map((s) => s.replace(/[.+?^${}()|[\]\\]/g, '\\$&')).join('.*') + '$', 'i');

export function matchAppRule(
  processName?: string,
  windowTitle?: string,
  rules: AppRule[] = DEFAULT_APP_RULES,
): AppRule | undefined {
  const proc = (processName ?? '').trim().toLowerCase().replace(/\.exe$/i, '');
  const procWithExe = proc ? `${proc}.exe` : '';
  const title = (windowTitle ?? '').trim().toLowerCase();

  let found: AppRule | undefined;
  for (const r of rules) {
    const p = r.pattern.trim().toLowerCase();
    if (!p) continue;
    const re = globToRegExp(p);
    const reNoExe = globToRegExp(p.replace(/\.exe$/i, ''));
    const hit =
      (proc && (re.test(proc) || re.test(procWithExe) || reNoExe.test(proc))) ||
      (title && p.length > 4 && title.includes(p.replace(/\*/g, '').replace(/\.exe$/i, '')));
    if (hit && (!found || (found.mode === 'readonly' && r.mode === 'block'))) {
      found = r;
    }
  }
  return found;
}

// ---------------------------------------------------------------------------
// Path Normalization & Protection (§7.3)
// ---------------------------------------------------------------------------
export function expandEnvVars(rawPath: string, env: Record<string, string | undefined> = process.env): string {
  return rawPath.replace(/%([^%]+)%/g, (_, name: string) => {
    const val = env[name] ?? env[name.toUpperCase()] ?? env[name.toLowerCase()];
    return val ?? `%${name}%`;
  });
}

/**
 * Normalizes a Windows path for security comparisons:
 * - Strips extended-length prefixes (`\\?\`, `\\.\`)
 * - Expands `%ENV%` variables
 * - Replaces 8.3 short aliases (`progra~1` -> `program files`, `progra~2` -> `program files (x86)`)
 * - Resolves `.` and `..` segments
 * - Lowercases and removes trailing slashes
 */
export function normalizeWindowsPath(rawPath: string, env: Record<string, string | undefined> = process.env): string {
  if (!rawPath) return '';
  let p = expandEnvVars(rawPath.trim(), env);
  p = p.replace(/^\\\\[?.]\\UNC\\/i, '\\\\');
  p = p.replace(/^\\\\[?.]\\/, '');
  p = p.replace(/\//g, '\\');

  // Expand common Windows 8.3 short names on C:
  p = p.replace(/^([a-zA-Z]:)\\progra~1(\\|$)/i, '$1\\Program Files$2');
  p = p.replace(/^([a-zA-Z]:)\\progra~2(\\|$)/i, '$1\\Program Files (x86)$2');
  p = p.replace(/^([a-zA-Z]:)\\progra~3(\\|$)/i, '$1\\ProgramData$2');

  // Normalize .. and . segments using win32 path rules
  p = path.win32.normalize(p);
  if (p.length > 3 && p.endsWith('\\')) {
    p = p.slice(0, -1);
  }
  return p.toLowerCase();
}

export type PathAccessResult =
  | { status: 'safe' }
  | { status: 'confirm_destructive'; reason: string }
  | { status: 'confirm_critical'; reason: string }
  | { status: 'hard_deny'; reason: string };

export function checkPathAccess(
  targetPath: string,
  operation: 'read' | 'write' | 'delete',
  extraSafeDirs: string[] = [],
  env: Record<string, string | undefined> = process.env,
): PathAccessResult {
  const norm = normalizeWindowsPath(targetPath, env);
  if (!norm) return { status: 'safe' };

  const home = normalizeWindowsPath(env.USERPROFILE || os.homedir(), env);
  const localAppData = normalizeWindowsPath(env.LOCALAPPDATA || path.win32.join(home, 'appdata\\local'), env);
  const appData = normalizeWindowsPath(env.APPDATA || path.win32.join(home, 'appdata\\roaming'), env);

  const isInside = (child: string, parent: string): boolean => {
    if (!parent) return false;
    return child === parent || child.startsWith(parent + '\\');
  };

  // 1. Hard deny even for READ:
  // - Prodesk & Prowser tokens/settings
  const hardDenyAll: Array<[string, string]> = [
    [path.win32.join(localAppData, 'prodesk\\helper.token'), 'Prodesk security token cannot be accessed by AI'],
    [path.win32.join(localAppData, 'prodesk\\settings.json'), 'Prodesk security settings cannot be modified or read by AI'],
    [path.win32.join(localAppData, 'prowser\\hub.token'), 'Prowser security token cannot be accessed by AI'],
    [path.win32.join(home, '.ssh'), 'SSH private keys directory (.ssh) is protected'],
    [path.win32.join(home, '.gnupg'), 'GPG keys directory (.gnupg) is protected'],
    [path.win32.join(localAppData, 'microsoft\\edge\\user data'), 'Browser User Data (cookies/passwords) is protected'],
    [path.win32.join(localAppData, 'google\\chrome\\user data'), 'Browser User Data (cookies/passwords) is protected'],
    [path.win32.join(localAppData, 'bravesoftware\\brave-browser\\user data'), 'Browser User Data (cookies/passwords) is protected'],
    [path.win32.join(appData, 'mozilla\\firefox\\profiles'), 'Firefox profile data (cookies/passwords) is protected'],
    [path.win32.join(localAppData, '1password'), 'Password manager data directory is protected'],
    [path.win32.join(appData, 'bitwarden'), 'Password manager data directory is protected'],
  ];

  for (const [dir, reason] of hardDenyAll) {
    if (isInside(norm, dir)) {
      return { status: 'hard_deny', reason };
    }
  }

  // 2. Hard deny for WRITE/DELETE: AI's own config directory
  const geminiConfig = path.win32.join(home, '.gemini\\config');
  if (operation !== 'read' && isInside(norm, geminiConfig)) {
    return { status: 'hard_deny', reason: 'AI configuration directory (.gemini/config) cannot be modified via file tools' };
  }

  if (operation === 'read') {
    return { status: 'safe' };
  }

  // 3. Critical confirmation for system directories on write/delete
  const systemDirs: Array<[string, string]> = [
    ['c:\\windows', 'Windows system directory (C:\\Windows)'],
    ['c:\\program files', 'Program Files directory'],
    ['c:\\program files (x86)', 'Program Files (x86) directory'],
    ['c:\\programdata', 'ProgramData system directory'],
    ['c:\\system volume information', 'System Volume Information'],
  ];

  for (const [dir, label] of systemDirs) {
    if (isInside(norm, dir)) {
      return { status: 'confirm_critical', reason: `Modifies protected system path: ${label}` };
    }
  }

  // 4. Safe user workspaces
  const tempDir = normalizeWindowsPath(env.TEMP || env.TMP || path.win32.join(localAppData, 'temp'), env);
  const safeDirs = [
    path.win32.join(home, 'desktop'),
    path.win32.join(home, 'documents'),
    path.win32.join(home, 'downloads'),
    path.win32.join(home, 'pictures'),
    path.win32.join(home, 'videos'),
    path.win32.join(home, 'music'),
    tempDir,
    ...extraSafeDirs.map((d) => normalizeWindowsPath(d, env)),
  ];

  for (const safe of safeDirs) {
    if (isInside(norm, safe)) {
      return { status: 'safe' };
    }
  }

  return {
    status: 'confirm_destructive',
    reason: `Path "${targetPath}" is outside standard safe workspace folders`,
  };
}

// ---------------------------------------------------------------------------
// Policy Settings & Decision Engine (§7.1–7.6)
// ---------------------------------------------------------------------------
export interface PolicySettings {
  paused: boolean;
  autoApprove: boolean;
  allowUnsignedInstallers: boolean;
  writesPerMinute: number;
  waitForUserIdleMs: number;
  keepHelperAlive: boolean;
  emergencyHotkey: string;
  appRules: AppRule[];
  safeDirectories: string[];
}

export const DEFAULT_POLICY_SETTINGS: PolicySettings = {
  paused: false,
  autoApprove: false,
  allowUnsignedInstallers: false,
  writesPerMinute: 120,
  waitForUserIdleMs: 1500,
  keepHelperAlive: false,
  emergencyHotkey: 'Ctrl+Alt+Shift+X',
  appRules: DEFAULT_APP_RULES,
  safeDirectories: [],
};

export interface PolicyInput {
  tool: string;
  params: Record<string, unknown>;
  targetProcess?: string;
  targetTitle?: string;
  isPasswordField?: boolean;
  isUnsignedInstaller?: boolean;
  settings: PolicySettings;
  sessionAllows: Set<string>;
  writesLastMinute: number;
  env?: Record<string, string | undefined>;
}

export type PolicyDecision =
  | { action: 'allow'; risk: Risk; reasons: string[] }
  | { action: 'deny'; risk: Risk; code: 'denied' | 'paused' | 'blocked'; reason: string }
  | { action: 'confirm'; risk: Risk; reasons: string[]; allowSession: boolean; sessionKey: string };

export function evaluateEffectiveRisk(input: PolicyInput): {
  risk: Risk;
  reasons: string[];
  hardDenyReason?: string;
} {
  const { tool, params, isPasswordField, isUnsignedInstaller, settings, env } = input;
  let risk: Risk = TOOL_RISK[tool] ?? 'critical';
  const reasons: string[] = [];

  if (!(tool in TOOL_RISK)) {
    reasons.push(`Unknown tool "${tool}"`);
  }

  // `computer` umbrella tool risk refinement
  if (tool === 'computer') {
    const act = String(params.action ?? '');
    if (act === 'screenshot' || act === 'cursor_position' || act === 'wait') {
      risk = 'read';
    }
  }

  // Password field escalation
  if (isPasswordField && (tool === 'type_text' || tool === 'set_value' || tool === 'computer' || tool === 'press_key')) {
    risk = 'critical';
    reasons.push('Target element is a password field (IsPassword=true)');
  }

  // Permanent file deletion escalation
  if (tool === 'fs_delete' && params.permanent === true) {
    risk = 'critical';
    reasons.push('Permanent deletion (bypasses Recycle Bin)');
  }

  // Unsigned installer escalation
  if (tool === 'run_installer' && isUnsignedInstaller) {
    risk = 'critical';
    reasons.push('Installer executable is not signed with a trusted Authenticode certificate');
  }

  // Command static scanner escalation
  if (tool === 'run_command') {
    const cmd = String(params.command ?? '');
    const hits = scanCommand(cmd);
    if (hits.length > 0) {
      risk = 'critical';
      reasons.push(...hits.map((h) => `Command ${h}`));
    }
  }

  // File path checks for fs_* and download/installer tools
  const pathParams: Array<{ pathVal: string; op: 'read' | 'write' | 'delete' }> = [];
  if (tool === 'fs_read' || tool === 'fs_info' || tool === 'fs_list' || tool === 'verify_file') {
    if (typeof params.path === 'string') pathParams.push({ pathVal: params.path, op: 'read' });
  } else if (tool === 'fs_write' || tool === 'fs_mkdir') {
    if (typeof params.path === 'string') pathParams.push({ pathVal: params.path, op: 'write' });
  } else if (tool === 'fs_delete') {
    if (typeof params.path === 'string') pathParams.push({ pathVal: params.path, op: 'delete' });
  } else if (tool === 'fs_copy' || tool === 'fs_move') {
    if (typeof params.source === 'string') {
      pathParams.push({ pathVal: params.source, op: tool === 'fs_move' ? 'delete' : 'read' });
    }
    if (typeof params.destination === 'string') {
      pathParams.push({ pathVal: params.destination, op: 'write' });
    }
  } else if (tool === 'download_file' && typeof params.destination === 'string') {
    pathParams.push({ pathVal: params.destination, op: 'write' });
  } else if (tool === 'screenshot' && typeof params.save_path === 'string') {
    pathParams.push({ pathVal: params.save_path, op: 'write' });
  }

  for (const { pathVal, op } of pathParams) {
    const res = checkPathAccess(pathVal, op, settings.safeDirectories, env);
    if (res.status === 'hard_deny') {
      return { risk: 'critical', reasons: [res.reason], hardDenyReason: res.reason };
    }
    if (res.status === 'confirm_critical') {
      risk = 'critical';
      reasons.push(res.reason);
    } else if (res.status === 'confirm_destructive') {
      risk = maxRisk(risk, 'destructive');
      reasons.push(res.reason);
    }
  }

  return { risk, reasons };
}

export function decidePolicy(input: PolicyInput): PolicyDecision {
  const { tool, targetProcess, targetTitle, settings } = input;
  const { risk, reasons, hardDenyReason } = evaluateEffectiveRisk(input);

  // 1. Kill switch / Paused
  if (settings.paused) {
    return {
      action: 'deny',
      risk,
      code: 'paused',
      reason: 'Prodesk kullanıcı tarafından duraklatıldı (Acil Durdurma / Duraklat aktif).',
    };
  }

  // 2. Hard-denied file paths (cannot be bypassed by autoApprove)
  if (hardDenyReason) {
    return {
      action: 'deny',
      risk: 'critical',
      code: 'blocked',
      reason: hardDenyReason,
    };
  }

  // 3. Hard-denied applications (cannot be bypassed by autoApprove)
  const appRule = matchAppRule(targetProcess, targetTitle, settings.appRules);
  if (appRule) {
    if (appRule.mode === 'block') {
      return {
        action: 'deny',
        risk: 'critical',
        code: 'blocked',
        reason: `Target application is blocked by security rule: ${appRule.reason}`,
      };
    }
    if (appRule.mode === 'readonly' && risk !== 'read') {
      return {
        action: 'deny',
        risk: 'critical',
        code: 'blocked',
        reason: `Target application is read-only by security rule: ${appRule.reason}`,
      };
    }
  }

  // 4. Rate limit check for write/destructive/critical actions
  if (isWriteOrAbove(risk) && input.writesLastMinute >= settings.writesPerMinute) {
    return {
      action: 'deny',
      risk,
      code: 'denied',
      reason: `Rate limit reached (${settings.writesPerMinute} write actions/minute). Wait before retrying.`,
    };
  }

  // 5. Unsigned installer check: requires explicit confirmation even if autoApprove is true, unless allowUnsignedInstallers is true
  if (tool === 'run_installer' && input.isUnsignedInstaller && !settings.allowUnsignedInstallers) {
    return {
      action: 'confirm',
      risk: 'critical',
      reasons: reasons.length ? reasons : ['Unsigned installer requires explicit confirmation'],
      allowSession: false,
      sessionKey: `${tool}|unsigned`,
    };
  }

  // 6. Auto-Approve bypasses destructive & critical confirmations
  if (settings.autoApprove) {
    return { action: 'allow', risk, reasons };
  }

  // 7. Read & standard write actions are allowed
  if (risk === 'read' || risk === 'write') {
    return { action: 'allow', risk, reasons };
  }

  // 8. Destructive actions can be allowed for the session
  const sessionKey = `${tool}|${(targetProcess || '*').toLowerCase()}`;
  if (risk === 'destructive' && input.sessionAllows.has(sessionKey)) {
    return { action: 'allow', risk, reasons };
  }

  return {
    action: 'confirm',
    risk,
    reasons: reasons.length ? reasons : [`Tool "${tool}" is classified as ${risk}`],
    allowSession: risk === 'destructive',
    sessionKey,
  };
}
