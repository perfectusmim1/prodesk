using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using Prodesk.Helper.Capture;
using Prodesk.Helper.Clipboard;
using Prodesk.Helper.Input;
using Prodesk.Helper.Install;
using Prodesk.Helper.Native;
using Prodesk.Helper.Ocr;
using Prodesk.Helper.Policy;
using Prodesk.Helper.Recording;
using Prodesk.Helper.Ui;
using Prodesk.Helper.Uia;
using Prodesk.Helper.Windows;

namespace Prodesk.Helper.Ipc;

public class ConnectedClientInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "mcp-client";
    public int Pid { get; set; }
    public DateTime ConnectedAt { get; set; } = DateTime.UtcNow;
}

public class PipeServer : IDisposable
{
    private readonly string _pipeName;
    private readonly string _expectedToken;
    private readonly PolicyManager _policy;
    private readonly UiaEngine _uia;
    private readonly CaptureEngine _capture;
    private readonly OcrEngineService _ocr;
    private readonly ClipboardService _clipboard;
    private readonly ActionEngine _action;
    private readonly WindowAndAppManager _winMgr;
    private readonly RecordingEngine _recorder;
    private readonly HelperUiManager _ui;

    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, ConnectedClientInfo> _clients = new();
    private DateTime _lastClientActivityUtc = DateTime.UtcNow;

    public int ConnectedClientCount => _clients.Count;
    public DateTime LastClientActivityUtc => _lastClientActivityUtc;

    public PipeServer(
        string pipeName,
        string expectedToken,
        PolicyManager policy,
        UiaEngine uia,
        CaptureEngine capture,
        OcrEngineService ocr,
        ClipboardService clipboard,
        ActionEngine action,
        WindowAndAppManager winMgr,
        RecordingEngine recorder,
        HelperUiManager ui)
    {
        _pipeName = pipeName;
        _expectedToken = expectedToken;
        _policy = policy;
        _uia = uia;
        _capture = capture;
        _ocr = ocr;
        _clipboard = clipboard;
        _action = action;
        _winMgr = winMgr;
        _recorder = recorder;
        _ui = ui;
    }

    public void Start()
    {
        // Spawn 4 concurrent pipe accept loops so multiple MCP clients connect immediately without contention
        for (int i = 0; i < 4; i++)
        {
            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }
    }

    private NamedPipeServerStream CreateSecurePipeStream()
    {
        var pipeSecurity = new PipeSecurity();
        var currentUser = WindowsIdentity.GetCurrent().User;
        if (currentUser != null)
        {
            pipeSecurity.AddAccessRule(new PipeAccessRule(
                currentUser,
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
        }

        return NamedPipeServerStreamAcl.Create(
            _pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 65536,
            outBufferSize: 65536,
            pipeSecurity);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreateSecurePipeStream();
                await pipe.WaitForConnectionAsync(ct);
                _ = Task.Run(() => HandleClientConnectionAsync(pipe, ct), ct);
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                break;
            }
            catch
            {
                pipe?.Dispose();
                await Task.Delay(200, ct);
            }
        }
    }

    private async Task HandleClientConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        string connId = Guid.NewGuid().ToString("N")[..8];
        bool authenticated = false;
        string clientName = "mcp-client";
        var writeLock = new SemaphoreSlim(1, 1);

        try
        {
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 65536, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), bufferSize: 65536, leaveOpen: true) { AutoFlush = true };

            while (!ct.IsCancellationRequested && pipe.IsConnected)
            {
                string? line = await reader.ReadLineAsync(ct);
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                _lastClientActivityUtc = DateTime.UtcNow;

                _ = Task.Run(async () =>
                {
                    string responseJson = await ProcessRpcLineAsync(line, connId, () => authenticated, (name, pid) =>
                    {
                        authenticated = true;
                        clientName = name;
                        _clients[connId] = new ConnectedClientInfo { Id = connId, Name = name, Pid = pid };
                    }, () => clientName);

                    await writeLock.WaitAsync(ct);
                    try
                    {
                        if (pipe.IsConnected)
                        {
                            await writer.WriteLineAsync(responseJson);
                        }
                    }
                    finally
                    {
                        writeLock.Release();
                    }
                }, ct);
            }
        }
        catch { }
        finally
        {
            _clients.TryRemove(connId, out _);
            _lastClientActivityUtc = DateTime.UtcNow;
            try { pipe.Dispose(); } catch { }
        }
    }

    private async Task<string> ProcessRpcLineAsync(
        string line,
        string connId,
        Func<bool> isAuthenticated,
        Action<string, int> markAuthenticated,
        Func<string> getClientName)
    {
        object? reqId = null;
        string method = "";
        var sw = Stopwatch.StartNew();

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var idElem))
            {
                reqId = idElem.ValueKind == JsonValueKind.Number ? idElem.GetInt64() : idElem.GetString();
            }
            method = root.GetProperty("method").GetString() ?? "";
            JsonElement? paramsElem = root.TryGetProperty("params", out var pElem) ? pElem.Clone() : null;

            if (method == "auth")
            {
                string token = GetStr(paramsElem, "token") ?? "";
                string cName = GetStr(paramsElem, "client") ?? "mcp-client";
                int pid = GetInt(paramsElem, "pid") ?? 0;

                if (!string.Equals(token, _expectedToken, StringComparison.Ordinal))
                {
                    return BuildErrorResponse(reqId, "denied", "Geçersiz Prodesk helper güvenlik token'ı.");
                }
                markAuthenticated(cName, pid);
                return BuildSuccessResponse(reqId, new
                {
                    text = "authenticated",
                    data = new { version = "1.0.0", pipeName = _pipeName }
                });
            }

            if (!isAuthenticated())
            {
                return BuildErrorResponse(reqId, "denied", "İstemci kimliği doğrulanmadı (auth gerekli).");
            }

            string clientName = GetStr(paramsElem, "_client") ?? getClientName();
            var resultObj = await DispatchToolAsync(method, paramsElem, clientName, sw);
            return BuildSuccessResponse(reqId, resultObj);
        }
        catch (HelperErrorException hex)
        {
            return BuildErrorResponse(reqId, hex.ErrorCode, hex.Message);
        }
        catch (Exception ex)
        {
            var inner = ex is AggregateException agg && agg.InnerException is HelperErrorException innerHex
                ? innerHex
                : ex.InnerException as HelperErrorException;
            if (inner != null)
            {
                return BuildErrorResponse(reqId, inner.ErrorCode, inner.Message);
            }
            return BuildErrorResponse(reqId, "failed", ex.Message);
        }
    }

    private async Task<object> DispatchToolAsync(
        string method,
        JsonElement? p,
        string clientName,
        Stopwatch sw)
    {
        // Internal audit completion notification from hybrid tools
        if (method == "audit_complete")
        {
            return new { text = "ok" };
        }

        // Test/runtime policy configuration endpoint (only accessible by token-authenticated local MCP server)
        if (method == "configure_policy_runtime")
        {
            if (GetBool(p, "paused") is bool pausedVal) _policy.SetPaused(pausedVal);
            if (GetBool(p, "autoApprove") is bool autoVal) _policy.SetAutoApprove(autoVal);
            if (GetBool(p, "emergencyStop") == true) _ui.TriggerEmergencyStop(showBalloon: false);
            return new
            {
                text = "Policy updated.",
                data = new { paused = _policy.Settings.Paused, autoApprove = _policy.Settings.AutoApprove }
            };
        }

        // Resolve target window/element metadata beforehand if present so PolicyManager can enforce blocked-app & password rules
        string? targetProcess = GetStr(p, "targetProcess") ?? GetStr(p, "process");
        string? targetTitle = GetStr(p, "targetTitle");
        bool isPasswordField = false;
        bool isUnsignedInstaller = GetBool(p, "isUnsignedInstaller") ?? false;

        string? uid = GetStr(p, "uid");
        string? winQuery = GetStr(p, "window");
        if (!string.IsNullOrWhiteSpace(uid))
        {
            try
            {
                var el = await UiaEngine.RunMtaWithTimeoutAsync(() => _uia.ResolveElementByUid(uid), 3000);
                isPasswordField = el.IsPassword;
                Win32.GetWindowThreadProcessId(el.WindowHwnd, out uint pid);
                if (pid != 0)
                {
                    try { targetProcess = Process.GetProcessById((int)pid).ProcessName; } catch { }
                }
                targetTitle = Win32.GetWindowTitle(el.WindowHwnd);
            }
            catch { }
        }
        else if (!string.IsNullOrWhiteSpace(winQuery))
        {
            var win = _uia.ResolveWindow(winQuery);
            if (win != null)
            {
                targetProcess = win.ProcessName;
                targetTitle = win.Title;
            }
        }

        // Evaluate security policy in Helper (§7)
        string evalTool = method == "authorize_tool" ? (GetStr(p, "tool") ?? method) : method;
        JsonElement? evalParams = method == "authorize_tool" && p.HasValue && p.Value.TryGetProperty("params", out var innerP)
            ? innerP
            : p;

        var decision = _policy.Evaluate(evalTool, evalParams, targetProcess, targetTitle, isPasswordField, isUnsignedInstaller);
        string paramsMasked = PolicyManager.MaskSensitiveParams(evalTool, evalParams, isPasswordField);

        if (decision.Action == "deny")
        {
            _policy.WriteAudit(new AuditEntry
            {
                Client = clientName,
                Tool = evalTool,
                Risk = decision.Risk,
                Decision = "deny",
                TargetApp = targetProcess,
                TargetWindow = targetTitle,
                ParamsSummary = paramsMasked,
                DurationMs = sw.ElapsedMilliseconds,
                Error = decision.Reason
            });
            throw new HelperErrorException(decision.Code, decision.Reason);
        }

        if (decision.Action == "confirm")
        {
            var confirmRes = await _ui.RequestConfirmationAsync(
                clientName, evalTool, decision.Risk, decision.Reasons, paramsMasked, decision.AllowSession);

            if (confirmRes == ConfirmResult.Deny)
            {
                _policy.WriteAudit(new AuditEntry
                {
                    Client = clientName,
                    Tool = evalTool,
                    Risk = decision.Risk,
                    Decision = "user_denied",
                    TargetApp = targetProcess,
                    TargetWindow = targetTitle,
                    ParamsSummary = paramsMasked,
                    DurationMs = sw.ElapsedMilliseconds,
                    Error = "Kullanıcı onay penceresinde işlemi reddetti."
                });
                throw new HelperErrorException("denied", "Kullanıcı işlemi reddetti. Tekrar denemeyin, kullanıcıya danışın.");
            }
            if (confirmRes == ConfirmResult.AllowSession && !string.IsNullOrEmpty(decision.SessionKey))
            {
                _policy.AddSessionAllow(decision.SessionKey);
            }
        }

        if (method == "authorize_tool")
        {
            _policy.WriteAudit(new AuditEntry
            {
                Client = clientName,
                Tool = evalTool,
                Risk = decision.Risk,
                Decision = "allow",
                TargetApp = targetProcess,
                TargetWindow = targetTitle,
                ParamsSummary = paramsMasked,
                DurationMs = sw.ElapsedMilliseconds
            });
            return new { text = "authorized" };
        }

        try
        {
            var res = await ExecuteNativeMethodAsync(method, p);
            _policy.WriteAudit(new AuditEntry
            {
                Client = clientName,
                Tool = method,
                Risk = decision.Risk,
                Decision = "allow",
                TargetApp = targetProcess,
                TargetWindow = targetTitle,
                ParamsSummary = paramsMasked,
                DurationMs = sw.ElapsedMilliseconds
            });
            return res;
        }
        catch (Exception ex)
        {
            _policy.WriteAudit(new AuditEntry
            {
                Client = clientName,
                Tool = method,
                Risk = decision.Risk,
                Decision = "error",
                TargetApp = targetProcess,
                TargetWindow = targetTitle,
                ParamsSummary = paramsMasked,
                DurationMs = sw.ElapsedMilliseconds,
                Error = ex.Message
            });
            throw;
        }
    }

    private async Task<object> ExecuteNativeMethodAsync(string method, JsonElement? p)
    {
        switch (method)
        {
            case "status":
            {
                var monitors = _capture.ListMonitors();
                var ocrLangs = OcrEngineService.GetInstalledOcrLanguages();
                bool elevated = Win32.IsProcessElevated((uint)Environment.ProcessId);
                return new
                {
                    text = $"Prodesk Helper v1.0.0 | Clients={_clients.Count} | AutoApprove={_policy.Settings.AutoApprove} | Paused={_policy.Settings.Paused} | Monitors={monitors.Count} | OCR=[{string.Join(", ", ocrLangs)}]",
                    data = new
                    {
                        helperVersion = "1.0.0",
                        pipeName = _pipeName,
                        connectedClients = _clients.Values.Select(c => new { c.Id, c.Name, c.Pid, c.ConnectedAt }).ToList(),
                        paused = _policy.Settings.Paused,
                        autoApprove = _policy.Settings.AutoApprove,
                        allowUnsignedInstallers = _policy.Settings.AllowUnsignedInstallers,
                        elevated,
                        monitors = monitors.Select(m => new { m.Index, m.DeviceName, m.Primary, m.DpiX, m.Scale, m.Bounds }),
                        ocrLanguages = ocrLangs
                    }
                };
            }

            case "list_monitors":
            {
                var monitors = _capture.ListMonitors();
                string txt = string.Join("\n", monitors.Select(m =>
                    $"Monitor #{m.Index} ({m.DeviceName}){(m.Primary ? " [PRIMARY]" : "")}: {m.RawBounds.Width}x{m.RawBounds.Height} @({m.RawBounds.Left},{m.RawBounds.Top}) DPI={m.DpiX} ({m.Scale * 100}%)"));
                return new
                {
                    text = txt,
                    data = new { count = monitors.Count, monitors }
                };
            }

            case "get_cursor":
            {
                Win32.GetCursorPos(out var pt);
                ElementEntry? el = null;
                try
                {
                    el = await UiaEngine.RunMtaWithTimeoutAsync(() => _uia.GetElementAtPoint(pt.X, pt.Y), 3000);
                }
                catch { }
                return new
                {
                    text = $"Cursor @({pt.X},{pt.Y})" + (el != null ? $" -> {el.ControlTypeName} \"{el.Name}\" [{el.Uid}]" : ""),
                    data = new
                    {
                        x = pt.X,
                        y = pt.Y,
                        element = el != null ? new { uid = el.Uid, role = el.ControlTypeName, name = el.Name, automationId = el.AutomationId } : null
                    }
                };
            }

            case "screenshot":
            {
                string target = GetStr(p, "target") ?? (GetStr(p, "uid") != null ? "element" : GetStr(p, "window") != null ? "window" : "screen");
                string? winQuery = GetStr(p, "window");
                string? uid = GetStr(p, "uid");
                int? monIdx = GetInt(p, "monitor");
                bool annotate = GetBool(p, "annotate") ?? false;
                string format = GetStr(p, "format") ?? "jpeg";
                int quality = GetInt(p, "quality") ?? 80;
                int maxWidth = GetInt(p, "max_width") ?? 1280;
                bool includeCursor = GetBool(p, "include_cursor") ?? false;
                bool restoreMin = GetBool(p, "restore_if_minimized") ?? true;
                Win32.RECT? region = GetRect(p, "region");

                var shot = _capture.TakeScreenshot(target, winQuery, uid, monIdx, region, annotate, format, quality, maxWidth, includeCursor, restoreMin);
                var meta = new
                {
                    origin = new { x = shot.OriginX, y = shot.OriginY },
                    scale = shot.Scale,
                    monitor = monIdx,
                    width = shot.Width,
                    height = shot.Height,
                    physicalWidth = shot.PhysicalWidth,
                    physicalHeight = shot.PhysicalHeight
                };

                return new
                {
                    text = shot.Summary,
                    images = new[] { new { data = shot.Base64Data, mimeType = shot.MimeType } },
                    screenshotMeta = meta,
                    data = new { screenshotMeta = meta }
                };
            }

            case "snapshot":
            {
                string? winQuery = GetStr(p, "window");
                bool interactiveOnly = GetBool(p, "interactive_only") ?? true;
                int maxDepth = GetInt(p, "max_depth") ?? 10;
                int maxChars = GetInt(p, "max_chars") ?? 30000;
                bool diff = GetBool(p, "diff") ?? false;
                string? rootUid = GetStr(p, "root_uid");
                bool includeOffscreen = GetBool(p, "include_offscreen") ?? false;

                var (txt, data) = await UiaEngine.RunMtaWithTimeoutAsync(
                    () => _uia.BuildSnapshot(winQuery, interactiveOnly, maxDepth, maxChars, diff, rootUid, includeOffscreen),
                    10000);
                return new { text = txt, data, untrusted = true };
            }

            case "find":
            {
                string? winQuery = GetStr(p, "window");
                string? name = GetStr(p, "name");
                string? role = GetStr(p, "role");
                string? autoId = GetStr(p, "automationId");
                string? cls = GetStr(p, "className");
                bool exact = GetBool(p, "exact") ?? false;
                int maxRes = GetInt(p, "max_results") ?? 25;

                var elems = await UiaEngine.RunMtaWithTimeoutAsync(
                    () => _uia.FindElements(winQuery, name, role, autoId, cls, exact, maxRes),
                    8000);
                string txt = elems.Count > 0
                    ? string.Join("\n", elems.Select(e => $"{e.ControlTypeName} \"{e.Name}\" [{e.Uid}] id=\"{e.AutomationId}\" @{e.Bounds.Left},{e.Bounds.Top} {e.Bounds.Width}x{e.Bounds.Height}"))
                    : "No matching UIA elements found.";
                return new
                {
                    text = txt,
                    untrusted = true,
                    data = new
                    {
                        count = elems.Count,
                        elements = elems.Select(e => new
                        {
                            uid = e.Uid,
                            role = e.ControlTypeName,
                            name = e.Name,
                            automationId = e.AutomationId,
                            className = e.ClassName,
                            value = e.IsPassword ? "" : e.Value,
                            bounds = new { x = e.Bounds.Left, y = e.Bounds.Top, width = e.Bounds.Width, height = e.Bounds.Height }
                        })
                    }
                };
            }

            case "element_at":
            {
                int x = GetInt(p, "x") ?? 0;
                int y = GetInt(p, "y") ?? 0;
                var el = await UiaEngine.RunMtaWithTimeoutAsync(() => _uia.GetElementAtPoint(x, y), 4000);
                var patterns = el.Element != null ? UiaEngine.GetSupportedPatterns(el.Element) : new List<string>();
                return new
                {
                    text = $"{el.ControlTypeName} \"{el.Name}\" [{el.Uid}] @{el.Bounds.Left},{el.Bounds.Top} {el.Bounds.Width}x{el.Bounds.Height} patterns=[{string.Join(", ", patterns)}]",
                    untrusted = true,
                    data = new
                    {
                        uid = el.Uid,
                        wid = el.Wid,
                        role = el.ControlTypeName,
                        name = el.Name,
                        automationId = el.AutomationId,
                        className = el.ClassName,
                        value = el.IsPassword ? "" : el.Value,
                        bounds = new { x = el.Bounds.Left, y = el.Bounds.Top, width = el.Bounds.Width, height = el.Bounds.Height },
                        patterns
                    }
                };
            }

            case "get_element":
            {
                string uid = GetStr(p, "uid") ?? throw new HelperErrorException("bad_request", "uid gerekli.");
                var el = await UiaEngine.RunMtaWithTimeoutAsync(() => _uia.ResolveElementByUid(uid), 4000);
                var patterns = el.Element != null ? UiaEngine.GetSupportedPatterns(el.Element) : new List<string>();
                var info = new
                {
                    uid = el.Uid,
                    wid = el.Wid,
                    role = el.ControlTypeName,
                    name = el.Name,
                    automationId = el.AutomationId,
                    className = el.ClassName,
                    value = el.IsPassword ? "***" : el.Value,
                    isEnabled = el.IsEnabled,
                    isOffscreen = el.IsOffscreen,
                    isPassword = el.IsPassword,
                    hasKeyboardFocus = el.HasKeyboardFocus,
                    bounds = new { x = el.Bounds.Left, y = el.Bounds.Top, width = el.Bounds.Width, height = el.Bounds.Height },
                    patterns
                };
                return new
                {
                    text = JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }),
                    untrusted = true,
                    data = info
                };
            }

            case "ocr":
            {
                string? winQuery = GetStr(p, "window");
                int? monIdx = GetInt(p, "monitor");
                Win32.RECT? region = GetRect(p, "region");
                string? lang = GetStr(p, "language");
                var (fullText, lines, words) = await _ocr.RunOcrAsync(winQuery, monIdx, region, lang);
                string formatted = lines.Count > 0
                    ? string.Join("\n", lines.Select(l => $"@{l.X},{l.Y} ({l.Width}x{l.Height}): {l.Text}"))
                    : "(no text recognized)";
                return new
                {
                    text = formatted,
                    untrusted = true,
                    data = new { fullText, lineCount = lines.Count, wordCount = words.Count, lines, words }
                };
            }

            case "find_text":
            {
                string text = GetStr(p, "text") ?? throw new HelperErrorException("bad_request", "text gerekli.");
                bool exact = GetBool(p, "exact") ?? false;
                string? winQuery = GetStr(p, "window");
                string? lang = GetStr(p, "language");
                var matches = await _ocr.FindTextAsync(text, exact, winQuery, lang);
                string formatted = matches.Count > 0
                    ? string.Join("\n", matches.Select(m => $"[{m.Source}] \"{m.Text}\" center=({m.CenterX},{m.CenterY}) bounds=({m.X},{m.Y},{m.Width}x{m.Height}){(m.Uid != null ? $" uid={m.Uid}" : "")}"))
                    : $"Text \"{text}\" not found.";
                return new
                {
                    text = formatted,
                    untrusted = true,
                    data = new { count = matches.Count, matches }
                };
            }

            case "wait_for":
            {
                string? uid = GetStr(p, "uid");
                string? text = GetStr(p, "text");
                string? winQuery = GetStr(p, "window");
                string state = GetStr(p, "state") ?? "appear";
                int timeoutMs = Math.Clamp(GetInt(p, "timeout_ms") ?? 10000, 100, 30000);
                int intervalMs = Math.Clamp(GetInt(p, "interval_ms") ?? 200, 50, 2000);
                var (selName, selRole, selId, selClass, selWin, selExact) = GetSelector(p);
                string? effectiveWin = winQuery ?? selWin;

                int elapsed = 0;
                while (elapsed <= timeoutMs)
                {
                    bool found = false;
                    string detail = "";

                    try
                    {
                        if (!string.IsNullOrWhiteSpace(uid))
                        {
                            var el = await UiaEngine.RunMtaWithTimeoutAsync(() => _uia.ResolveElementByUid(uid), 2000);
                            found = state == "enabled" ? el.IsEnabled : state == "focused" ? el.HasKeyboardFocus : !el.IsOffscreen;
                            detail = $"[{el.Uid}] \"{el.Name}\"";
                        }
                        else if (!string.IsNullOrWhiteSpace(selName) || !string.IsNullOrWhiteSpace(selRole) || !string.IsNullOrWhiteSpace(selId) || !string.IsNullOrWhiteSpace(selClass))
                        {
                            var hits = await UiaEngine.RunMtaWithTimeoutAsync(
                                () => _uia.FindElements(effectiveWin, selName, selRole, selId, selClass, selExact, 3), 2500);
                            found = hits.Count > 0;
                            if (found) detail = $"[{hits[0].Uid}] \"{hits[0].Name}\"";
                        }
                        else if (!string.IsNullOrWhiteSpace(text))
                        {
                            var hits = await _ocr.FindTextAsync(text, exact: false, windowQuery: effectiveWin);
                            found = hits.Count > 0;
                            if (found) detail = $"\"{hits[0].Text}\" @({hits[0].CenterX},{hits[0].CenterY})";
                        }
                        else if (!string.IsNullOrWhiteSpace(effectiveWin))
                        {
                            var w = _uia.ResolveWindow(effectiveWin);
                            found = w != null;
                            if (w != null) detail = $"[{w.Wid}] \"{w.Title}\"";
                        }
                    }
                    catch { found = false; }

                    bool conditionMet = state == "disappear" ? !found : found;
                    if (conditionMet)
                    {
                        return new
                        {
                            text = $"Condition \"{state}\" met after {elapsed} ms: {detail}",
                            data = new { state, elapsedMs = elapsed, detail }
                        };
                    }

                    await Task.Delay(intervalMs);
                    elapsed += intervalMs;
                }

                throw new HelperErrorException("timeout", $"wait_for ({state}) {timeoutMs} ms içinde gerçekleşmedi.");
            }

            case "click":
            {
                var target = await ResolveActionTargetFromParamsAsync(p);
                string button = GetStr(p, "button") ?? "left";
                bool isDouble = GetBool(p, "double") ?? false;
                string mode = GetStr(p, "mode") ?? "auto";
                var (txt, ver, sel) = await _action.ClickAsync(
                    target.Element, target.ScreenX, target.ScreenY, target.TargetHwnd, button, isDouble, mode, target.ResolvedSelector);
                return new { text = txt, verification = MapVerification(ver), data = new { resolvedSelector = sel } };
            }

            case "hover":
            {
                var target = await ResolveActionTargetFromParamsAsync(p);
                int dur = GetInt(p, "duration_ms") ?? 150;
                _action.OnVisualAction?.Invoke(new VisualActionState
                {
                    Type = "hover",
                    X = target.ScreenX,
                    Y = target.ScreenY,
                    Description = $"Vurgulanıyor: ({target.ScreenX}, {target.ScreenY})",
                    DurationMs = dur
                });
                Win32.GetCursorPos(out var orig);
                Win32.SetCursorPos(target.ScreenX, target.ScreenY);
                if (dur > 0) await Task.Delay(dur);
                Win32.SetCursorPos(orig.X, orig.Y);
                return new { text = $"Hovered at ({target.ScreenX},{target.ScreenY}) for {dur} ms and restored cursor." };
            }

            case "mouse_move":
            {
                int x = GetInt(p, "x") ?? 0;
                int y = GetInt(p, "y") ?? 0;
                _action.OnVisualAction?.Invoke(new VisualActionState
                {
                    Type = "move",
                    X = x,
                    Y = y,
                    Description = $"İmleç taşınıyor: ({x}, {y})"
                });
                Win32.SetCursorPos(x, y);
                return new { text = $"Moved cursor to ({x},{y}).", data = new { x, y } };
            }

            case "drag":
            {
                int fromX = GetInt(p, "from_x") ?? 0;
                int fromY = GetInt(p, "from_y") ?? 0;
                int toX = GetInt(p, "to_x") ?? 0;
                int toY = GetInt(p, "to_y") ?? 0;
                string? fromUid = GetStr(p, "from_uid");
                string? toUid = GetStr(p, "to_uid");
                int dur = GetInt(p, "duration_ms") ?? 350;

                if (!string.IsNullOrWhiteSpace(fromUid))
                {
                    var el = await UiaEngine.RunMtaWithTimeoutAsync(() => _uia.ResolveElementByUid(fromUid), 3000);
                    fromX = el.Bounds.Left + el.Bounds.Width / 2;
                    fromY = el.Bounds.Top + el.Bounds.Height / 2;
                }
                if (!string.IsNullOrWhiteSpace(toUid))
                {
                    var el = await UiaEngine.RunMtaWithTimeoutAsync(() => _uia.ResolveElementByUid(toUid), 3000);
                    toX = el.Bounds.Left + el.Bounds.Width / 2;
                    toY = el.Bounds.Top + el.Bounds.Height / 2;
                }

                var (txt, ver) = await _action.DragAsync(fromX, fromY, toX, toY, dur);
                return new { text = txt, verification = MapVerification(ver) };
            }

            case "scroll":
            {
                var target = await ResolveActionTargetFromParamsAsync(p, allowTextQuery: false, defaultToForegroundOrCursor: true);
                int dx = GetInt(p, "dx") ?? 0;
                int dy = GetInt(p, "dy") ?? 3;
                string? toEdge = GetStr(p, "to");
                string mode = GetStr(p, "mode") ?? "auto";
                var (txt, ver) = await _action.ScrollAsync(target.Element, target.ScreenX, target.ScreenY, target.TargetHwnd, dx, dy, toEdge, mode);
                return new { text = txt, verification = MapVerification(ver) };
            }

            case "type_text":
            {
                var target = await ResolveActionTargetFromParamsAsync(p, allowTextQuery: false, defaultToForegroundOrCursor: true);
                string text = GetStr(p, "text") ?? "";
                bool clearFirst = GetBool(p, "clear_first") ?? false;
                bool submit = GetBool(p, "submit") ?? false;
                bool viaClipboard = GetBool(p, "via_clipboard") ?? false;
                string mode = GetStr(p, "mode") ?? "auto";

                var (txt, ver, sel) = await _action.TypeTextAsync(
                    target.Element, target.ScreenX, target.ScreenY, target.TargetHwnd, text, clearFirst, submit, viaClipboard, mode, target.ResolvedSelector);
                return new { text = txt, verification = MapVerification(ver), data = new { resolvedSelector = sel } };
            }

            case "press_key":
            {
                var chords = new List<string>();
                if (p.HasValue && p.Value.TryGetProperty("keys", out var kElem))
                {
                    if (kElem.ValueKind == JsonValueKind.String)
                        chords.Add(kElem.GetString() ?? "");
                    else if (kElem.ValueKind == JsonValueKind.Array)
                        chords.AddRange(kElem.EnumerateArray().Select(x => x.GetString() ?? ""));
                }
                string? winQuery = GetStr(p, "window");
                string? uid = GetStr(p, "uid");
                int repeat = GetInt(p, "repeat") ?? 1;
                string mode = GetStr(p, "mode") ?? "auto";
                var (txt, ver) = await _action.PressKeyAsync(chords, winQuery, uid, repeat, mode);
                return new { text = txt, verification = MapVerification(ver) };
            }

            case "set_value":
            {
                var target = await ResolveActionTargetFromParamsAsync(p);
                string valStr = p.HasValue && p.Value.TryGetProperty("value", out var vElem)
                    ? (vElem.ValueKind == JsonValueKind.String ? vElem.GetString() ?? "" : vElem.GetRawText())
                    : "";

                if (target.Element?.Element != null)
                {
                    bool ok = await UiaEngine.RunMtaWithTimeoutAsync(() =>
                    {
                        var el = target.Element.Element;
                        if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var vpObj) && vpObj is ValuePattern vp)
                        {
                            vp.SetValue(valStr);
                            return true;
                        }
                        if (double.TryParse(valStr, out double dVal) &&
                            el.TryGetCurrentPattern(RangeValuePattern.Pattern, out var rvObj) && rvObj is RangeValuePattern rv)
                        {
                            rv.SetValue(dVal);
                            return true;
                        }
                        return false;
                    }, 4000);
                    if (ok)
                    {
                        return new
                        {
                            text = $"Set value of [{target.Element.Uid}] via UIA pattern.",
                            verification = new { via = "uia", focusRestored = true, cursorRestored = true },
                            data = new { resolvedSelector = target.ResolvedSelector }
                        };
                    }
                }
                var (txt, ver, sel) = await _action.TypeTextAsync(
                    target.Element, target.ScreenX, target.ScreenY, target.TargetHwnd, valStr, clearFirst: true, submit: false, viaClipboard: false, mode: "auto", target.ResolvedSelector);
                return new { text = txt, verification = MapVerification(ver), data = new { resolvedSelector = sel } };
            }

            case "invoke":
            {
                var target = await ResolveActionTargetFromParamsAsync(p);
                var (txt, ver, sel) = await _action.ClickAsync(
                    target.Element, target.ScreenX, target.ScreenY, target.TargetHwnd, "left", false, "auto", target.ResolvedSelector);
                return new { text = txt, verification = MapVerification(ver), data = new { resolvedSelector = sel } };
            }

            case "toggle":
            {
                var target = await ResolveActionTargetFromParamsAsync(p);
                string desired = GetStr(p, "state") ?? "toggle";
                if (target.Element?.Element != null)
                {
                    string finalState = await UiaEngine.RunMtaWithTimeoutAsync(() =>
                    {
                        var el = target.Element.Element;
                        if (el.TryGetCurrentPattern(TogglePattern.Pattern, out var tpObj) && tpObj is TogglePattern tp)
                        {
                            var cur = tp.Current.ToggleState;
                            if (desired == "toggle" ||
                                (desired == "on" && cur != ToggleState.On) ||
                                (desired == "off" && cur != ToggleState.Off))
                            {
                                tp.Toggle();
                            }
                            return tp.Current.ToggleState.ToString();
                        }
                        return "";
                    }, 3000);

                    if (!string.IsNullOrEmpty(finalState))
                    {
                        return new
                        {
                            text = $"Toggled [{target.Element.Uid}] -> {finalState}",
                            verification = new { via = "uia", focusRestored = true, cursorRestored = true }
                        };
                    }
                }
                var (txt, ver, _) = await _action.ClickAsync(target.Element, target.ScreenX, target.ScreenY, target.TargetHwnd);
                return new { text = txt, verification = MapVerification(ver) };
            }

            case "select_option":
            {
                var target = await ResolveActionTargetFromParamsAsync(p);
                string? optionText = GetStr(p, "option");
                int? index = GetInt(p, "index");
                if (target.Element?.Element == null)
                    throw new HelperErrorException("bad_request", "select_option için bir UIA öğesi (uid veya selector) gereklidir.");

                string selectedName = await UiaEngine.RunMtaWithTimeoutAsync(() =>
                {
                    var root = target.Element.Element;
                    if (root.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expObj) && expObj is ExpandCollapsePattern exp)
                    {
                        try { exp.Expand(); Thread.Sleep(80); } catch { }
                    }

                    var children = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                    int curIdx = 0;
                    foreach (AutomationElement child in children)
                    {
                        string cName = child.Current.Name ?? "";
                        bool hit = (!string.IsNullOrEmpty(optionText) && cName.Contains(optionText, StringComparison.OrdinalIgnoreCase)) ||
                                   (index.HasValue && curIdx == index.Value);
                        if (child.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var siObj) && siObj is SelectionItemPattern si)
                        {
                            if (hit)
                            {
                                si.Select();
                                return cName;
                            }
                            curIdx++;
                        }
                    }
                    throw new HelperErrorException("not_found", $"Seçenek bulunamadı (option={optionText}, index={index}).");
                }, 4000);

                return new
                {
                    text = $"Selected option \"{selectedName}\" in [{target.Element.Uid}].",
                    verification = new { via = "uia", focusRestored = true, cursorRestored = true }
                };
            }

            case "expand":
            case "collapse":
            {
                var target = await ResolveActionTargetFromParamsAsync(p);
                if (target.Element?.Element == null)
                    throw new HelperErrorException("bad_request", $"{method} için bir UIA öğesi gereklidir.");
                await UiaEngine.RunMtaWithTimeoutAsync(() =>
                {
                    var el = target.Element.Element;
                    if (el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expObj) && expObj is ExpandCollapsePattern exp)
                    {
                        if (method == "expand") exp.Expand();
                        else exp.Collapse();
                        return true;
                    }
                    throw new HelperErrorException("failed", $"Öğe [{target.Element.Uid}] ExpandCollapsePattern desteklemiyor.");
                }, 3000);
                return new
                {
                    text = $"{method} executed on [{target.Element.Uid}].",
                    verification = new { via = "uia", focusRestored = true, cursorRestored = true }
                };
            }

            case "focus":
            {
                var target = await ResolveActionTargetFromParamsAsync(p);
                if (target.Element?.Element != null)
                {
                    await UiaEngine.RunMtaWithTimeoutAsync(() =>
                    {
                        target.Element.Element.SetFocus();
                        return true;
                    }, 3000);
                }
                return new { text = $"Focused [{target.Element?.Uid ?? "target"}]." };
            }

            case "list_windows":
            {
                bool incMin = GetBool(p, "include_minimized") ?? true;
                string? filter = GetStr(p, "filter");
                var wins = _uia.EnumerateTopWindows(incMin, filter);
                string txt = wins.Count > 0
                    ? string.Join("\n", wins.Select(w => $"[{w.Wid}] \"{w.Title}\" (app={w.ProcessName}.exe, pid={w.Pid}, state={w.State}{(w.IsForeground ? ", foreground" : "")}{(w.IsElevated ? ", elevated" : "")}) @{w.Bounds.Left},{w.Bounds.Top} {w.Bounds.Width}x{w.Bounds.Height}"))
                    : "No matching windows.";
                return new
                {
                    text = txt,
                    untrusted = true,
                    data = new
                    {
                        count = wins.Count,
                        windows = wins.Select(w => new
                        {
                            wid = w.Wid,
                            hwnd = w.Hwnd.ToInt64(),
                            title = w.Title,
                            process = w.ProcessName,
                            pid = w.Pid,
                            className = w.ClassName,
                            state = w.State,
                            isForeground = w.IsForeground,
                            isElevated = w.IsElevated,
                            bounds = new { x = w.Bounds.Left, y = w.Bounds.Top, width = w.Bounds.Width, height = w.Bounds.Height }
                        })
                    }
                };
            }

            case "focus_window":
            {
                string winQuery = GetStr(p, "window") ?? throw new HelperErrorException("bad_request", "window gerekli.");
                var win = _uia.ResolveWindow(winQuery) ?? throw new HelperErrorException("not_found", $"Pencere bulunamadı: {winQuery}");
                ActionEngine.BringWindowToFrontInternal(win.Hwnd);
                return new { text = $"Focused window [{win.Wid}] \"{win.Title}\".", data = new { wid = win.Wid, title = win.Title } };
            }

            case "window_action":
            {
                string winQuery = GetStr(p, "window") ?? throw new HelperErrorException("bad_request", "window gerekli.");
                string act = GetStr(p, "action") ?? "restore";
                var (txt, data) = _winMgr.PerformWindowAction(winQuery, act, GetInt(p, "x"), GetInt(p, "y"), GetInt(p, "width"), GetInt(p, "height"));
                return new { text = txt, data };
            }

            case "launch_app":
            {
                string? path = GetStr(p, "path");
                string? name = GetStr(p, "name");
                string? aumid = GetStr(p, "aumid");
                string[]? args = GetStrArray(p, "args");
                string? cwd = GetStr(p, "cwd");
                bool bg = GetBool(p, "background") ?? true;
                bool waitWin = GetBool(p, "wait_for_window") ?? true;
                int timeoutMs = GetInt(p, "timeout_ms") ?? 8000;
                var (txt, data) = await _winMgr.LaunchAppAsync(path, name, aumid, args, cwd, bg, waitWin, timeoutMs);
                return new { text = txt, data };
            }

            case "close_app":
            {
                var (txt, data) = await _winMgr.CloseAppAsync(
                    GetStr(p, "window"), GetStr(p, "process"), GetInt(p, "pid"), GetBool(p, "force") ?? false);
                return new { text = txt, data };
            }

            case "list_apps":
            {
                var apps = _winMgr.ListInstalledApps(GetStr(p, "filter"));
                return new { text = $"Found {apps.Count} installed application(s).", data = new { count = apps.Count, apps }, untrusted = true };
            }

            case "open_path":
            {
                string target = GetStr(p, "target") ?? throw new HelperErrorException("bad_request", "target gerekli.");
                var (txt, data) = SystemAndVerifyService.OpenPath(target);
                return new { text = txt, data };
            }

            case "fs_recycle":
            {
                string target = GetStr(p, "path") ?? throw new HelperErrorException("bad_request", "path gerekli.");
                var (txt, data) = SystemAndVerifyService.MoveToRecycleBin(target);
                return new { text = txt, data };
            }

            case "verify_file":
            {
                string target = GetStr(p, "path") ?? throw new HelperErrorException("bad_request", "path gerekli.");
                var (txt, data) = SystemAndVerifyService.VerifyFileSignature(target);
                return new { text = txt, data };
            }

            case "clipboard_read":
            {
                string fmt = GetStr(p, "format") ?? "text";
                var (txt, data, imgB64) = _clipboard.ReadClipboard(fmt);
                return new
                {
                    text = txt,
                    untrusted = true,
                    data,
                    images = imgB64 != null ? new[] { new { data = imgB64, mimeType = "image/png" } } : null
                };
            }

            case "clipboard_write":
            {
                string fmt = GetStr(p, "format") ?? "text";
                var (txt, data) = _clipboard.WriteClipboard(fmt, GetStr(p, "text"), GetStrArray(p, "files"));
                return new { text = txt, data };
            }

            case "clipboard_history":
            {
                int maxItems = GetInt(p, "max_items") ?? 25;
                var (txt, data) = await _clipboard.GetClipboardHistoryAsync(maxItems);
                return new { text = txt, untrusted = true, data };
            }

            case "clipboard_clear":
            {
                _clipboard.ClearClipboard();
                return new { text = "Clipboard cleared." };
            }

            case "list_processes":
            {
                var (txt, data) = SystemAndVerifyService.ListProcesses(GetStr(p, "filter"), GetInt(p, "max_results") ?? 100);
                return new { text = txt, untrusted = true, data };
            }

            case "kill_process":
            {
                var (txt, data) = SystemAndVerifyService.KillProcess(GetInt(p, "pid"), GetStr(p, "name"), GetBool(p, "tree") ?? true);
                return new { text = txt, data };
            }

            case "system_info":
            {
                var (txt, data) = SystemAndVerifyService.GetSystemInfo(_capture);
                return new { text = txt, data };
            }

            case "notify":
            {
                string title = GetStr(p, "title") ?? "Prodesk";
                string msg = GetStr(p, "message") ?? "";
                string level = GetStr(p, "level") ?? "info";
                _ui.ShowNotification(title, msg, level);
                return new { text = $"Notification displayed: {title}" };
            }

            case "record_start":
            {
                var (txt, data) = _recorder.StartRecording(
                    GetStr(p, "target") ?? "screen",
                    GetStr(p, "window"),
                    GetInt(p, "monitor"),
                    GetRect(p, "region"),
                    GetInt(p, "fps") ?? 10,
                    GetInt(p, "max_seconds") ?? 120,
                    GetBool(p, "include_cursor") ?? true);
                return new { text = txt, data };
            }

            case "record_stop":
            {
                var (txt, data) = await _recorder.StopRecordingAsync(GetStr(p, "save_path"));
                return new { text = txt, data };
            }

            case "record_to_gif":
            {
                double secs = p.HasValue && p.Value.TryGetProperty("seconds", out var sEl) && sEl.ValueKind == JsonValueKind.Number
                    ? sEl.GetDouble() : 3.0;
                var (txt, data) = await _recorder.RecordToGifAsync(
                    secs,
                    GetInt(p, "fps") ?? 8,
                    GetInt(p, "max_width") ?? 640,
                    GetStr(p, "save_path"),
                    GetStr(p, "window"));
                return new { text = txt, data };
            }

            default:
                throw new HelperErrorException("bad_request", $"Bilinmeyen helper metodu: {method}");
        }
    }

    private async Task<(ElementEntry? Element, int ScreenX, int ScreenY, IntPtr TargetHwnd, object? ResolvedSelector)> ResolveActionTargetFromParamsAsync(
        JsonElement? p,
        bool allowTextQuery = true,
        bool defaultToForegroundOrCursor = false)
    {
        string? uid = GetStr(p, "uid");
        string? winQuery = GetStr(p, "window");
        string? textQuery = allowTextQuery ? GetStr(p, "text") : null;
        int? x = GetInt(p, "x");
        int? y = GetInt(p, "y");
        var (selName, selRole, selId, selClass, selWin, selExact) = GetSelector(p);
        return await _action.ResolveTargetAsync(
            uid, winQuery ?? selWin, selName, selRole, selId, selClass, selExact, textQuery, x, y, defaultToForegroundOrCursor);
    }

    private static object MapVerification(ActionVerificationDto v) => new
    {
        via = v.Via,
        focusRestored = v.FocusRestored,
        cursorRestored = v.CursorRestored,
        activeWindowChanged = v.ActiveWindowChanged,
        newWindows = v.NewWindows,
        closedWindows = v.ClosedWindows,
        summary = v.Summary
    };

    private static string? GetStr(JsonElement? p, string key)
    {
        if (!p.HasValue || p.Value.ValueKind != JsonValueKind.Object) return null;
        if (p.Value.TryGetProperty(key, out var val))
        {
            if (val.ValueKind == JsonValueKind.String) return val.GetString();
            if (val.ValueKind == JsonValueKind.Number) return val.GetRawText();
        }
        return null;
    }

    private static int? GetInt(JsonElement? p, string key)
    {
        if (!p.HasValue || p.Value.ValueKind != JsonValueKind.Object) return null;
        if (p.Value.TryGetProperty(key, out var val))
        {
            if (val.ValueKind == JsonValueKind.Number && val.TryGetInt32(out int v)) return v;
            if (val.ValueKind == JsonValueKind.Number) return (int)Math.Round(val.GetDouble());
        }
        return null;
    }

    private static bool? GetBool(JsonElement? p, string key)
    {
        if (!p.HasValue || p.Value.ValueKind != JsonValueKind.Object) return null;
        if (p.Value.TryGetProperty(key, out var val))
        {
            if (val.ValueKind == JsonValueKind.True) return true;
            if (val.ValueKind == JsonValueKind.False) return false;
        }
        return null;
    }

    private static string[]? GetStrArray(JsonElement? p, string key)
    {
        if (!p.HasValue || p.Value.ValueKind != JsonValueKind.Object) return null;
        if (p.Value.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.Array)
        {
            return val.EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
        }
        return null;
    }

    private static Win32.RECT? GetRect(JsonElement? p, string key)
    {
        if (!p.HasValue || p.Value.ValueKind != JsonValueKind.Object) return null;
        if (p.Value.TryGetProperty(key, out var r) && r.ValueKind == JsonValueKind.Object)
        {
            int x = GetInt(r, "x") ?? 0;
            int y = GetInt(r, "y") ?? 0;
            int w = GetInt(r, "width") ?? 0;
            int h = GetInt(r, "height") ?? 0;
            if (w > 0 && h > 0)
            {
                return new Win32.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
            }
        }
        return null;
    }

    private static (string? Name, string? Role, string? AutomationId, string? ClassName, string? Window, bool Exact) GetSelector(JsonElement? p)
    {
        if (!p.HasValue || p.Value.ValueKind != JsonValueKind.Object)
            return (null, null, null, null, null, false);
        if (p.Value.TryGetProperty("selector", out var s) && s.ValueKind == JsonValueKind.Object)
        {
            return (
                GetStr(s, "name"),
                GetStr(s, "role"),
                GetStr(s, "automationId"),
                GetStr(s, "className"),
                GetStr(s, "window"),
                GetBool(s, "exact") ?? false
            );
        }
        return (null, null, null, null, null, false);
    }

    private static string BuildSuccessResponse(object? id, object result)
    {
        return JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            result
        });
    }

    private static string BuildErrorResponse(object? id, string errorCode, string message)
    {
        return JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            error = new
            {
                code = -32000,
                message,
                data = new { errorCode }
            }
        });
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        _cts.Dispose();
    }
}
