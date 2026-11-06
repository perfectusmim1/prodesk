using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Prodesk.Helper.Policy;

public class AppRuleDto
{
    [JsonPropertyName("pattern")]
    public string Pattern { get; set; } = "";

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "block"; // "block" | "readonly"

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";
}

public class HelperSettings
{
    [JsonPropertyName("paused")]
    public bool Paused { get; set; } = false;

    [JsonPropertyName("autoApprove")]
    public bool AutoApprove { get; set; } = false;

    [JsonPropertyName("allowUnsignedInstallers")]
    public bool AllowUnsignedInstallers { get; set; } = false;

    [JsonPropertyName("writesPerMinute")]
    public int WritesPerMinute { get; set; } = 120;

    [JsonPropertyName("waitForUserIdleMs")]
    public int WaitForUserIdleMs { get; set; } = 0;

    [JsonPropertyName("restoreFocusAfterAction")]
    public bool RestoreFocusAfterAction { get; set; } = false;

    [JsonPropertyName("restoreCursorAfterAction")]
    public bool RestoreCursorAfterAction { get; set; } = false;

    [JsonPropertyName("keepHelperAlive")]
    public bool KeepHelperAlive { get; set; } = false;

    [JsonPropertyName("emergencyHotkey")]
    public string EmergencyHotkey { get; set; } = "Ctrl+Alt+Shift+X";

    [JsonPropertyName("appRules")]
    public List<AppRuleDto> AppRules { get; set; } = DefaultAppRules();

    [JsonPropertyName("safeDirectories")]
    public List<string> SafeDirectories { get; set; } = new();

    public static List<AppRuleDto> DefaultAppRules() => new()
    {
        new() { Pattern = "1password*", Mode = "block", Reason = "Parola yöneticisi (1Password)" },
        new() { Pattern = "bitwarden*", Mode = "block", Reason = "Parola yöneticisi (Bitwarden)" },
        new() { Pattern = "keepass*", Mode = "block", Reason = "Parola yöneticisi (KeePass/KeePassXC)" },
        new() { Pattern = "lastpass*", Mode = "block", Reason = "Parola yöneticisi (LastPass)" },
        new() { Pattern = "dashlane*", Mode = "block", Reason = "Parola yöneticisi (Dashlane)" },
        new() { Pattern = "protonpass*", Mode = "block", Reason = "Parola yöneticisi (Proton Pass)" },
        new() { Pattern = "nordpass*", Mode = "block", Reason = "Parola yöneticisi (NordPass)" },
        new() { Pattern = "credentialuibroker*", Mode = "block", Reason = "Windows Kimlik Bilgisi Aracısı" },
        new() { Pattern = "consent.exe", Mode = "block", Reason = "UAC Yönetici Onay Penceresi" },
        new() { Pattern = "sechealthui*", Mode = "block", Reason = "Windows Güvenliği" },
        new() { Pattern = "lsass.exe", Mode = "block", Reason = "Windows LSASS Süreci" },
        new() { Pattern = "mmc.exe", Mode = "block", Reason = "Microsoft Yönetim Konsolu" },
        new() { Pattern = "regedit*", Mode = "readonly", Reason = "Kayıt Defteri Düzenleyicisi (Salt Okunur)" },
    };
}

public class AuditEntry
{
    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = DateTime.UtcNow.ToString("O");

    [JsonPropertyName("client")]
    public string Client { get; set; } = "mcp-client";

    [JsonPropertyName("tool")]
    public string Tool { get; set; } = "";

    [JsonPropertyName("risk")]
    public string Risk { get; set; } = "read";

    [JsonPropertyName("decision")]
    public string Decision { get; set; } = "allow";

    [JsonPropertyName("targetApp")]
    public string? TargetApp { get; set; }

    [JsonPropertyName("targetWindow")]
    public string? TargetWindow { get; set; }

    [JsonPropertyName("paramsSummary")]
    public string? ParamsSummary { get; set; }

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public class PolicyManager
{
    private readonly object _lock = new();
    private readonly Queue<DateTime> _writeTimestamps = new();
    private readonly HashSet<string> _sessionAllows = new(StringComparer.OrdinalIgnoreCase);

    public string DataDir { get; }
    public string SettingsPath => Path.Combine(DataDir, "settings.json");
    public string AuditDir => Path.Combine(DataDir, "audit");
    public HelperSettings Settings { get; private set; } = new();

    public event Action? SettingsChanged;

    private static readonly Dictionary<string, string> ToolRiskMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["status"] = "read",
        ["list_monitors"] = "read",
        ["get_cursor"] = "read",
        ["wait"] = "read",
        ["batch"] = "read",
        ["computer"] = "write",
        ["screenshot"] = "read",
        ["snapshot"] = "read",
        ["find"] = "read",
        ["element_at"] = "read",
        ["get_element"] = "read",
        ["ocr"] = "read",
        ["find_text"] = "read",
        ["wait_for"] = "read",
        ["click"] = "write",
        ["hover"] = "write",
        ["mouse_move"] = "write",
        ["drag"] = "write",
        ["scroll"] = "write",
        ["type_text"] = "write",
        ["press_key"] = "write",
        ["set_value"] = "write",
        ["invoke"] = "write",
        ["toggle"] = "write",
        ["select_option"] = "write",
        ["expand"] = "write",
        ["collapse"] = "write",
        ["focus"] = "write",
        ["list_windows"] = "read",
        ["focus_window"] = "write",
        ["window_action"] = "write",
        ["launch_app"] = "write",
        ["close_app"] = "destructive",
        ["list_apps"] = "read",
        ["open_path"] = "write",
        ["fs_list"] = "read",
        ["fs_info"] = "read",
        ["fs_read"] = "read",
        ["fs_write"] = "write",
        ["fs_copy"] = "write",
        ["fs_move"] = "destructive",
        ["fs_mkdir"] = "write",
        ["fs_delete"] = "destructive",
        ["fs_recycle"] = "destructive",
        ["fs_search"] = "read",
        ["reveal_in_explorer"] = "read",
        ["download_file"] = "destructive",
        ["github_release"] = "destructive",
        ["winget_search"] = "read",
        ["winget_list"] = "read",
        ["winget_install"] = "destructive",
        ["winget_uninstall"] = "destructive",
        ["verify_file"] = "read",
        ["run_installer"] = "destructive",
        ["clipboard_read"] = "read",
        ["clipboard_write"] = "write",
        ["clipboard_history"] = "destructive",
        ["clipboard_clear"] = "write",
        ["run_command"] = "destructive",
        ["command_status"] = "read",
        ["command_input"] = "write",
        ["command_kill"] = "write",
        ["list_processes"] = "read",
        ["kill_process"] = "destructive",
        ["system_info"] = "read",
        ["notify"] = "write",
        ["record_start"] = "write",
        ["record_stop"] = "read",
        ["record_to_gif"] = "write",
        ["session_report"] = "read",
        ["macro_record_start"] = "write",
        ["macro_record_stop"] = "read",
        ["macro_run"] = "write",
        ["macro_list"] = "read",
        ["macro_delete"] = "destructive",
        ["browser"] = "write",
    };

    private static readonly (Regex Pattern, string Label)[] DangerousCommandRules =
    {
        (new Regex(@"\bRemove-Item\b(?=.*(?:\s-(?:Recurse|r|Force|f)\b))", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Remove-Item -Recurse/-Force"),
        (new Regex(@"\b(?:rm|del|erase)\b\s+(?:-[rRfF]{1,4}|/[sSqQfF])\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Toplu dosya silme (rm/del)"),
        (new Regex(@"\b(?:rd|rmdir)\b\s+/[sSqQ]", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Klasör ağacı silme (rmdir /s)"),
        (new Regex(@"\bformat(?:\.com|\.exe)?\s+[a-zA-Z]:", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Disk biçimlendirme (format)"),
        (new Regex(@"\bdiskpart\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Disk bölümleme (diskpart)"),
        (new Regex(@"\bbcdedit\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Önyükleme yapılandırması (bcdedit)"),
        (new Regex(@"\breg(?:\.exe)?\s+(?:delete|add|import|restore|load)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Kayıt Defteri değişikliği (reg.exe)"),
        (new Regex(@"\b(?:Set|New|Remove|Clear)-ItemProperty\b.*\bHKLM:", RegexOptions.IgnoreCase | RegexOptions.Compiled), "HKLM Kayıt Defteri değişikliği"),
        (new Regex(@"\bSet-ExecutionPolicy\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "PowerShell ExecutionPolicy değişikliği"),
        (new Regex(@"\b(?:iwr|Invoke-WebRequest|irm|Invoke-RestMethod|curl|wget)\b[\s\S]*\|\s*(?:iex|Invoke-Expression)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Uzaktan script indirme ve çalıştırma (iwr | iex)"),
        (new Regex(@"\bDownloadString\s*\(|\bDownloadFile\s*\(", RegexOptions.IgnoreCase | RegexOptions.Compiled), "WebClient ile uzaktan dosya/kod indirme"),
        (new Regex(@"\b(?:iex|Invoke-Expression)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Dinamik komut çalıştırma (Invoke-Expression)"),
        (new Regex(@"\b(?:shutdown(?:\.exe)?|Stop-Computer|Restart-Computer)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Bilgisayarı kapatma/yeniden başlatma"),
        (new Regex(@"\bcipher(?:\.exe)?\s+/w", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Disk boş alan silme (cipher /w)"),
        (new Regex(@"\bvssadmin\b[\s\S]*\bdelete\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Gölge kopya silme (vssadmin)"),
        (new Regex(@"\bwmic\b[\s\S]*\bshadowcopy\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Gölge kopya müdahalesi (wmic)"),
        (new Regex(@"\b(?:netsh\b[\s\S]*\badvfirewall\b|Set-NetFirewallProfile\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Güvenlik duvarı değişikliği"),
        (new Regex(@"\b(?:Add|Set)-MpPreference\b|-DisableRealtimeMonitoring\b|-ExclusionPath\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Windows Defender istisna/koruma değişikliği"),
        (new Regex(@"\btakeown(?:\.exe)?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Sistem dosyası sahipliği alma (takeown)"),
        (new Regex(@"\bicacls(?:\.exe)?\b[\s\S]*/(?:grant|setowner|remove)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Dosya izinleri değiştirme (icacls)"),
        (new Regex(@"\s-(?:EncodedCommand|enc|e)\s+[A-Za-z0-9+/=]{8,}", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Gizlenmiş Base64 PowerShell komutu"),
    };

    public PolicyManager(string? customDataDir = null)
    {
        DataDir = customDataDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Prodesk");
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(AuditDir);
        LoadSettings();
        CleanupOldAuditLogs();
    }

    public void LoadSettings()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    string json = File.ReadAllText(SettingsPath);
                    var loaded = JsonSerializer.Deserialize<HelperSettings>(json);
                    if (loaded != null)
                    {
                        Settings = loaded;
                        if (Settings.AppRules == null || Settings.AppRules.Count == 0)
                            Settings.AppRules = HelperSettings.DefaultAppRules();
                        return;
                    }
                }
            }
            catch
            {
                // fallback to defaults
            }
            Settings = new HelperSettings();
            SaveSettings();
        }
    }

    public void SaveSettings()
    {
        lock (_lock)
        {
            try
            {
                string json = JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsPath, json);
            }
            catch
            {
                // ignore
            }
        }
        SettingsChanged?.Invoke();
    }

    public void SetPaused(bool paused)
    {
        lock (_lock)
        {
            Settings.Paused = paused;
            SaveSettings();
        }
    }

    public void SetAutoApprove(bool autoApprove)
    {
        lock (_lock)
        {
            Settings.AutoApprove = autoApprove;
            SaveSettings();
        }
    }

    public void AddSessionAllow(string sessionKey)
    {
        lock (_lock)
        {
            _sessionAllows.Add(sessionKey);
        }
    }

    public static List<string> ScanCommand(string? command)
    {
        var hits = new List<string>();
        if (string.IsNullOrWhiteSpace(command)) return hits;
        foreach (var (re, label) in DangerousCommandRules)
        {
            if (re.IsMatch(command)) hits.Add(label);
        }
        return hits;
    }

    public AppRuleDto? MatchAppRule(string? processName, string? windowTitle)
    {
        string proc = (processName ?? "").Trim().ToLowerInvariant();
        if (proc.EndsWith(".exe")) proc = proc[..^4];
        string procExe = string.IsNullOrEmpty(proc) ? "" : proc + ".exe";
        string title = (windowTitle ?? "").Trim().ToLowerInvariant();

        AppRuleDto? found = null;
        foreach (var r in Settings.AppRules)
        {
            string p = (r.Pattern ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(p)) continue;
            string regexPattern = "^" + Regex.Escape(p).Replace("\\*", ".*") + "$";
            string regexNoExe = "^" + Regex.Escape(p.EndsWith(".exe") ? p[..^4] : p).Replace("\\*", ".*") + "$";
            bool hit = (!string.IsNullOrEmpty(proc) &&
                        (Regex.IsMatch(proc, regexPattern, RegexOptions.IgnoreCase) ||
                         Regex.IsMatch(procExe, regexPattern, RegexOptions.IgnoreCase) ||
                         Regex.IsMatch(proc, regexNoExe, RegexOptions.IgnoreCase))) ||
                       (!string.IsNullOrEmpty(title) && p.Length > 4 &&
                        title.Contains(p.Replace("*", "").Replace(".exe", "")));

            if (hit && (found == null || (found.Mode == "readonly" && r.Mode == "block")))
            {
                found = r;
            }
        }
        return found;
    }

    public (string Action, string Risk, string Code, string Reason, List<string> Reasons, bool AllowSession, string SessionKey) Evaluate(
        string tool,
        JsonElement? paramsElem,
        string? targetProcess,
        string? targetTitle,
        bool isPasswordField,
        bool isUnsignedInstaller)
    {
        lock (_lock)
        {
            string risk = ToolRiskMap.TryGetValue(tool, out var baseRisk) ? baseRisk : "critical";
            var reasons = new List<string>();

            if (Settings.Paused)
            {
                return ("deny", risk, "paused", "Prodesk kullanıcı tarafından duraklatıldı (Acil Durdurma aktif).", reasons, false, "");
            }

            // Check blocked app rules (hard deny even with Auto-Approve)
            var appRule = MatchAppRule(targetProcess, targetTitle);
            if (appRule != null)
            {
                if (appRule.Mode == "block")
                {
                    return ("deny", "critical", "blocked", $"Hedef uygulama güvenlik kuralıyla engellendi: {appRule.Reason}", reasons, false, "");
                }
                if (appRule.Mode == "readonly" && risk != "read")
                {
                    return ("deny", "critical", "blocked", $"Hedef uygulama salt-okunur korumalı: {appRule.Reason}", reasons, false, "");
                }
            }

            // Check password field escalation
            if (isPasswordField && (tool is "type_text" or "set_value" or "press_key" or "computer"))
            {
                risk = "critical";
                reasons.Add("Hedef öğe bir parola alanı (IsPassword=true)");
            }

            // Check permanent delete
            if (tool == "fs_delete" && paramsElem.HasValue &&
                paramsElem.Value.TryGetProperty("permanent", out var permProp) && permProp.ValueKind == JsonValueKind.True)
            {
                risk = "critical";
                reasons.Add("Kalıcı dosya silme (Geri Dönüşüm Kutusu atlanıyor)");
            }

            // Check unsigned installer
            if (tool == "run_installer" && isUnsignedInstaller)
            {
                risk = "critical";
                reasons.Add("Yükleyici dosyası geçerli bir Authenticode dijital imzasına sahip değil");
            }

            // Check command static scanner
            if (tool == "run_command" && paramsElem.HasValue &&
                paramsElem.Value.TryGetProperty("command", out var cmdProp) && cmdProp.ValueKind == JsonValueKind.String)
            {
                var cmdHits = ScanCommand(cmdProp.GetString());
                if (cmdHits.Count > 0)
                {
                    risk = "critical";
                    reasons.AddRange(cmdHits.Select(h => $"Komut riski: {h}"));
                }
            }

            // Check protected file paths
            if (paramsElem.HasValue)
            {
                foreach (string propName in new[] { "path", "source", "destination", "save_path" })
                {
                    if (paramsElem.Value.TryGetProperty(propName, out var pVal) && pVal.ValueKind == JsonValueKind.String)
                    {
                        string rawPath = pVal.GetString() ?? "";
                        bool isWriteOp = risk != "read" || propName is "destination" or "save_path";
                        var pathCheck = CheckPath(rawPath, isWriteOp);
                        if (pathCheck.Status == "hard_deny")
                        {
                            return ("deny", "critical", "blocked", pathCheck.Reason, reasons, false, "");
                        }
                        if (pathCheck.Status == "confirm_critical")
                        {
                            risk = "critical";
                            reasons.Add(pathCheck.Reason);
                        }
                        else if (pathCheck.Status == "confirm_destructive" && risk == "write")
                        {
                            risk = "destructive";
                            reasons.Add(pathCheck.Reason);
                        }
                    }
                }
            }

            // Rate limit check for write/destructive/critical
            if (risk != "read")
            {
                var cutoff = DateTime.UtcNow.AddMinutes(-1);
                while (_writeTimestamps.Count > 0 && _writeTimestamps.Peek() < cutoff)
                {
                    _writeTimestamps.Dequeue();
                }
                if (_writeTimestamps.Count >= Settings.WritesPerMinute)
                {
                    return ("deny", risk, "denied", $"Dakikalık işlem sınırı aşıldı ({Settings.WritesPerMinute} işlem/dk).", reasons, false, "");
                }
                _writeTimestamps.Enqueue(DateTime.UtcNow);
            }

            // Unsigned installer requires explicit confirmation even if AutoApprove is on, unless AllowUnsignedInstallers is true
            if (tool == "run_installer" && isUnsignedInstaller && !Settings.AllowUnsignedInstallers)
            {
                return ("confirm", "critical", "", "", reasons, false, $"{tool}|unsigned");
            }

            // Auto-Approve bypasses destructive & critical confirmations
            if (Settings.AutoApprove)
            {
                return ("allow", risk, "", "", reasons, false, "");
            }

            if (risk is "read" or "write")
            {
                return ("allow", risk, "", "", reasons, false, "");
            }

            string sessionKey = $"{tool}|{(targetProcess ?? "*").ToLowerInvariant()}";
            if (risk == "destructive" && _sessionAllows.Contains(sessionKey))
            {
                return ("allow", risk, "", "", reasons, false, sessionKey);
            }

            if (reasons.Count == 0)
            {
                reasons.Add($"\"{tool}\" aracı {risk} risk sınıfında yer alıyor.");
            }
            return ("confirm", risk, "", "", reasons, risk == "destructive", sessionKey);
        }
    }

    private (string Status, string Reason) CheckPath(string rawPath, bool isWrite)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return ("safe", "");
        string expanded = Environment.ExpandEnvironmentVariables(rawPath.Trim());
        expanded = expanded.Replace('/', '\\');
        if (expanded.StartsWith(@"\\?\")) expanded = expanded[4..];
        expanded = Regex.Replace(expanded, @"^([a-zA-Z]:)\\progra~1(\\|$)", @"$1\Program Files$2", RegexOptions.IgnoreCase);
        expanded = Regex.Replace(expanded, @"^([a-zA-Z]:)\\progra~2(\\|$)", @"$1\Program Files (x86)$2", RegexOptions.IgnoreCase);

        string full;
        try
        {
            full = Path.GetFullPath(expanded).TrimEnd('\\').ToLowerInvariant();
        }
        catch
        {
            full = expanded.TrimEnd('\\').ToLowerInvariant();
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\').ToLowerInvariant();
        string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData).TrimEnd('\\').ToLowerInvariant();
        string roamingApp = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData).TrimEnd('\\').ToLowerInvariant();

        static bool Inside(string child, string parent) =>
            !string.IsNullOrEmpty(parent) && (child == parent || child.StartsWith(parent + "\\"));

        (string Path, string Reason)[] hardDeny =
        {
            (Path.Combine(localApp, @"prodesk\helper.token"), "Prodesk güvenlik token dosyasına erişilemez."),
            (Path.Combine(localApp, @"prodesk\settings.json"), "Prodesk güvenlik ayarları AI tarafından değiştirilemez."),
            (Path.Combine(localApp, @"prowser\hub.token"), "Prowser güvenlik token dosyasına erişilemez."),
            (Path.Combine(home, ".ssh"), "SSH anahtar klasörü (.ssh) korumalıdır."),
            (Path.Combine(home, ".gnupg"), "GPG anahtar klasörü (.gnupg) korumalıdır."),
            (Path.Combine(localApp, @"microsoft\edge\user data"), "Tarayıcı profil verileri (çerez/parola) korumalıdır."),
            (Path.Combine(localApp, @"google\chrome\user data"), "Tarayıcı profil verileri (çerez/parola) korumalıdır."),
            (Path.Combine(roamingApp, @"mozilla\firefox\profiles"), "Tarayıcı profil verileri (çerez/parola) korumalıdır."),
        };

        foreach (var item in hardDeny)
        {
            if (Inside(full, item.Path.ToLowerInvariant()))
                return ("hard_deny", item.Reason);
        }

        if (isWrite && Inside(full, Path.Combine(home, @".gemini\config").ToLowerInvariant()))
        {
            return ("hard_deny", "AI yapılandırma klasörü (.gemini/config) dosya araçlarıyla değiştirilemez.");
        }

        if (!isWrite) return ("safe", "");

        string[] sysDirs = { @"c:\windows", @"c:\program files", @"c:\program files (x86)", @"c:\programdata" };
        foreach (var s in sysDirs)
        {
            if (Inside(full, s))
                return ("confirm_critical", $"Sistem klasörüne yazma/silme işlemi: {s}");
        }

        return ("safe", "");
    }

    public static string MaskSensitiveParams(string tool, JsonElement? paramsElem, bool isPasswordField)
    {
        if (!paramsElem.HasValue) return "{}";
        try
        {
            if (isPasswordField || tool is "clipboard_write")
            {
                return "{\"masked\":\"***REDACTED***\"}";
            }
            string raw = paramsElem.Value.GetRawText();
            if (raw.Length > 300) raw = raw[..300] + "...";
            return raw;
        }
        catch
        {
            return "{}";
        }
    }

    public void WriteAudit(AuditEntry entry)
    {
        try
        {
            string day = DateTime.UtcNow.ToString("yyyy-MM-dd");
            string file = Path.Combine(AuditDir, $"audit-{day}.jsonl");
            string line = JsonSerializer.Serialize(entry) + Environment.NewLine;
            lock (_lock)
            {
                File.AppendAllText(file, line);
            }
        }
        catch
        {
            // ignore
        }
    }

    public List<AuditEntry> ReadRecentAuditEntries(int maxCount = 200)
    {
        var list = new List<AuditEntry>();
        try
        {
            var files = Directory.GetFiles(AuditDir, "audit-*.jsonl")
                .OrderByDescending(f => f)
                .Take(3);
            foreach (var f in files)
            {
                var lines = File.ReadAllLines(f);
                for (int i = lines.Length - 1; i >= 0 && list.Count < maxCount; i--)
                {
                    if (string.IsNullOrWhiteSpace(lines[i])) continue;
                    var item = JsonSerializer.Deserialize<AuditEntry>(lines[i]);
                    if (item != null) list.Add(item);
                }
            }
        }
        catch
        {
            // ignore
        }
        return list;
    }

    private void CleanupOldAuditLogs()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-30);
            foreach (var f in Directory.GetFiles(AuditDir, "audit-*.jsonl"))
            {
                if (File.GetLastWriteTimeUtc(f) < cutoff)
                {
                    File.Delete(f);
                }
            }
        }
        catch
        {
            // ignore
        }
    }
}
