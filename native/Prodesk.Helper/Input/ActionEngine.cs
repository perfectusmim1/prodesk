using System.Runtime.InteropServices;
using System.Windows.Automation;
using Prodesk.Helper.Clipboard;
using Prodesk.Helper.Native;
using Prodesk.Helper.Ocr;
using Prodesk.Helper.Policy;
using Prodesk.Helper.Uia;

namespace Prodesk.Helper.Input;

public class VisualActionState
{
    public string Type { get; set; } = "click"; // "click" | "type" | "drag" | "key" | "scroll" | "hover"
    public int X { get; set; }
    public int Y { get; set; }
    public int? FromX { get; set; }
    public int? FromY { get; set; }
    public int? ToX { get; set; }
    public int? ToY { get; set; }
    public string Description { get; set; } = "";
    public int DurationMs { get; set; } = 150;
}

public class ActionVerificationDto
{
    public string Via { get; set; } = "uia";
    public bool FocusRestored { get; set; } = true;
    public bool CursorRestored { get; set; } = true;
    public bool ActiveWindowChanged { get; set; }
    public bool TitleChanged { get; set; }
    public List<object> NewWindows { get; set; } = new();
    public List<string> ClosedWindows { get; set; } = new();
    public string Summary { get; set; } = "";
}

public class ActionEngine
{
    private readonly UiaEngine _uia;
    private readonly OcrEngineService _ocr;
    private readonly ClipboardService _clipboard;
    private readonly PolicyManager _policy;

    public Action<Win32.RECT?>? OnOverlayUpdate { get; set; }
    public Action<VisualActionState>? OnVisualAction { get; set; }
    public Action? OnEmergencyStopTriggered { get; set; }

    public ActionEngine(UiaEngine uia, OcrEngineService ocr, ClipboardService clipboard, PolicyManager policy)
    {
        _uia = uia;
        _ocr = ocr;
        _clipboard = clipboard;
        _policy = policy;
    }

    public async Task<(ElementEntry? Element, int ScreenX, int ScreenY, IntPtr TargetHwnd, object? ResolvedSelector)> ResolveTargetAsync(
        string? uid,
        string? windowQuery,
        string? selectorName,
        string? selectorRole,
        string? selectorAutomationId,
        string? selectorClassName,
        bool selectorExact,
        string? textQuery,
        int? x,
        int? y,
        bool defaultToForegroundOrCursor = false)
    {
        if (!string.IsNullOrWhiteSpace(uid))
        {
            var el = await UiaEngine.RunMtaWithTimeoutAsync(() => _uia.ResolveElementByUid(uid), 5000);
            int cx = el.Bounds.Left + Math.Max(2, el.Bounds.Width / 2);
            int cy = el.Bounds.Top + Math.Max(2, el.Bounds.Height / 2);
            var sel = new
            {
                name = el.Name,
                role = el.ControlTypeName,
                automationId = el.AutomationId,
                window = el.Wid
            };
            return (el, cx, cy, el.NativeHwnd != IntPtr.Zero ? el.NativeHwnd : el.WindowHwnd, sel);
        }

        if (!string.IsNullOrWhiteSpace(selectorName) ||
            !string.IsNullOrWhiteSpace(selectorRole) ||
            !string.IsNullOrWhiteSpace(selectorAutomationId) ||
            !string.IsNullOrWhiteSpace(selectorClassName))
        {
            var matches = await UiaEngine.RunMtaWithTimeoutAsync(
                () => _uia.FindElements(windowQuery, selectorName, selectorRole, selectorAutomationId, selectorClassName, selectorExact, 5),
                6000);
            var first = matches.FirstOrDefault()
                ?? throw new HelperErrorException("not_found", $"Seçiciyle eşleşen UIA öğesi bulunamadı (name={selectorName}, role={selectorRole}, id={selectorAutomationId}).");
            int cx = first.Bounds.Left + Math.Max(2, first.Bounds.Width / 2);
            int cy = first.Bounds.Top + Math.Max(2, first.Bounds.Height / 2);
            var sel = new
            {
                name = first.Name,
                role = first.ControlTypeName,
                automationId = first.AutomationId,
                window = windowQuery ?? first.Wid
            };
            return (first, cx, cy, first.NativeHwnd != IntPtr.Zero ? first.NativeHwnd : first.WindowHwnd, sel);
        }

        if (!string.IsNullOrWhiteSpace(textQuery))
        {
            var textHits = await _ocr.FindTextAsync(textQuery, exact: false, windowQuery: windowQuery);
            var firstHit = textHits.FirstOrDefault()
                ?? throw new HelperErrorException("not_found", $"Ekranda veya pencerede \"{textQuery}\" metni bulunamadı (UIA + OCR).");
            if (!string.IsNullOrEmpty(firstHit.Uid))
            {
                var el = await UiaEngine.RunMtaWithTimeoutAsync(() => _uia.ResolveElementByUid(firstHit.Uid!), 4000);
                return (el, firstHit.CenterX, firstHit.CenterY, el.WindowHwnd, new { text = textQuery, window = windowQuery });
            }
            var win = _uia.ResolveWindow(windowQuery);
            return (null, firstHit.CenterX, firstHit.CenterY, win?.Hwnd ?? IntPtr.Zero, new { text = textQuery, window = windowQuery });
        }

        if (x.HasValue && y.HasValue)
        {
            IntPtr hwnd = Win32.WindowFromPoint(new Win32.POINT { X = x.Value, Y = y.Value });
            ElementEntry? ptEl = null;
            try
            {
                ptEl = await UiaEngine.RunMtaWithTimeoutAsync(() => _uia.GetElementAtPoint(x.Value, y.Value), 2500);
            }
            catch { }
            return (ptEl, x.Value, y.Value, hwnd, null);
        }

        if (!string.IsNullOrWhiteSpace(windowQuery))
        {
            var win = _uia.ResolveWindow(windowQuery)
                ?? throw new HelperErrorException("not_found", $"Pencere bulunamadı: \"{windowQuery}\"");
            int cx = win.Bounds.Left + Math.Max(10, win.Bounds.Width / 2);
            int cy = win.Bounds.Top + Math.Max(10, win.Bounds.Height / 2);
            return (null, cx, cy, win.Hwnd, new { window = windowQuery });
        }

        if (defaultToForegroundOrCursor)
        {
            Win32.GetCursorPos(out var curPt);
            IntPtr fgHwnd = Win32.GetForegroundWindow();
            if (fgHwnd == IntPtr.Zero) fgHwnd = Win32.WindowFromPoint(curPt);
            return (null, curPt.X, curPt.Y, fgHwnd, null);
        }

        throw new HelperErrorException("bad_request", "Hedef belirtilmedi: uid, selector, text, window veya (x, y) koordinatı verin.");
    }

    private void CheckTargetWindowSecurity(IntPtr hwnd)
    {
        if (Win32.IsSecureDesktopActive())
        {
            throw new HelperErrorException("secure_desktop", "Güvenli masaüstü (UAC / Winlogon) aktifken otomasyon yapılamaz.");
        }
        if (hwnd == IntPtr.Zero) return;
        Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid != 0 && Win32.IsProcessElevated(pid) && !Win32.IsProcessElevated((uint)Environment.ProcessId))
        {
            throw new HelperErrorException("elevated_target", "Hedef pencere yönetici (elevated) yetkisiyle çalışıyor (UIPI sınırı).");
        }
    }

    private async Task EnsureUserIdleForPhysicalInputAsync()
    {
        int waitMs = Math.Clamp(_policy.Settings.WaitForUserIdleMs, 0, 10_000);
        if (waitMs <= 0) return;

        uint idle = Win32.GetIdleTimeMs();
        if (idle < 400)
        {
            // Wait up to waitForUserIdleMs for user to stop moving mouse/typing (§4.1)
            int elapsed = 0;
            while (elapsed < waitMs)
            {
                await Task.Delay(200);
                elapsed += 200;
                if (Win32.GetIdleTimeMs() >= 500) return;
            }
            throw new HelperErrorException("user_busy", "Kullanıcı şu anda fare veya klavyeyi aktif kullanıyor (user_busy). Kısa süre sonra tekrar deneyin.");
        }
    }

    private void CheckCornerFailsafe()
    {
        if (Win32.GetCursorPos(out var pt) && pt.X <= 2 && pt.Y <= 2 && pt.X >= -2 && pt.Y >= -2)
        {
            _policy.SetPaused(true);
            ReleaseAllModifiers();
            OnEmergencyStopTriggered?.Invoke();
            throw new HelperErrorException("interrupted_by_user", "İmleç sol üst köşeye (0,0 failsafe) götürüldüğü için Prodesk duraklatıldı.");
        }
    }

    public async Task<(string Text, ActionVerificationDto Verification, object? ResolvedSelector)> ClickAsync(
        ElementEntry? element,
        int screenX,
        int screenY,
        IntPtr targetHwnd,
        string button = "left",
        bool isDouble = false,
        string mode = "auto",
        object? resolvedSelector = null)
    {
        CheckTargetWindowSecurity(targetHwnd);
        OnVisualAction?.Invoke(new VisualActionState
        {
            Type = "click",
            X = screenX,
            Y = screenY,
            Description = $"Tıklanıyor: {(string.IsNullOrEmpty(element?.Name) ? $"({screenX}, {screenY})" : element.Name)}"
        });

        var beforeWins = _uia.EnumerateTopWindows();
        IntPtr origFg = Win32.GetForegroundWindow();
        Win32.GetCursorPos(out var origCursor);

        string usedTier = "uia";

        // Tier 1: UIA Patterns (left single click on element)
        if ((mode is "auto" or "uia") && element?.Element != null && button == "left" && !isDouble)
        {
            bool uiaOk = await UiaEngine.RunMtaWithTimeoutAsync(() =>
            {
                var el = element.Element;
                if (el.TryGetCurrentPattern(InvokePattern.Pattern, out var invObj) && invObj is InvokePattern inv)
                {
                    inv.Invoke();
                    return true;
                }
                if (el.TryGetCurrentPattern(TogglePattern.Pattern, out var togObj) && togObj is TogglePattern tog)
                {
                    tog.Toggle();
                    return true;
                }
                if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selObj) && selObj is SelectionItemPattern sel)
                {
                    sel.Select();
                    return true;
                }
                if (el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expObj) && expObj is ExpandCollapsePattern exp)
                {
                    if (exp.Current.ExpandCollapseState == ExpandCollapseState.Collapsed) exp.Expand();
                    else exp.Collapse();
                    return true;
                }
                return false;
            }, 4000);

            if (uiaOk)
            {
                await Task.Delay(25);
                var ver = BuildVerification("uia", origFg, origCursor, beforeWins);
                return ($"Clicked [{element.Uid}] \"{element.Name}\" via UIA pattern (no focus/cursor disruption).", ver, resolvedSelector);
            }
            if (mode == "uia")
            {
                throw new HelperErrorException("failed", $"Öğe [{element.Uid}] doğrudan UIA tıklama pattern'ini (Invoke/Toggle/SelectionItem) desteklemiyor.");
            }
        }

        // Tier 2: Background Win32 Window Messages
        if (mode is "auto" or "message")
        {
            IntPtr msgHwnd = element?.NativeHwnd != IntPtr.Zero && element != null ? element.NativeHwnd : targetHwnd;
            if (msgHwnd != IntPtr.Zero)
            {
                var pt = new Win32.POINT { X = screenX, Y = screenY };
                IntPtr childHwnd = Win32.WindowFromPoint(pt);
                if (childHwnd != IntPtr.Zero) msgHwnd = childHwnd;

                var clientPt = new Win32.POINT { X = screenX, Y = screenY };
                Win32.ScreenToClient(msgHwnd, ref clientPt);
                IntPtr lParam = Win32.MakeLParam(clientPt.X, clientPt.Y);

                uint downMsg = button == "right" ? Win32.WM_RBUTTONDOWN : button == "middle" ? Win32.WM_MBUTTONDOWN : Win32.WM_LBUTTONDOWN;
                uint upMsg = button == "right" ? Win32.WM_RBUTTONUP : button == "middle" ? Win32.WM_MBUTTONUP : Win32.WM_LBUTTONUP;
                IntPtr wParam = (IntPtr)(button == "right" ? 2 : button == "middle" ? 16 : 1);

                Win32.PostMessage(msgHwnd, downMsg, wParam, lParam);
                Win32.PostMessage(msgHwnd, upMsg, IntPtr.Zero, lParam);
                if (isDouble && button == "left")
                {
                    Win32.PostMessage(msgHwnd, Win32.WM_LBUTTONDBLCLK, wParam, lParam);
                    Win32.PostMessage(msgHwnd, Win32.WM_LBUTTONUP, IntPtr.Zero, lParam);
                }

                await Task.Delay(25);
                if (mode == "message")
                {
                    var ver = BuildVerification("message", origFg, origCursor, beforeWins);
                    return ($"Clicked @({screenX},{screenY}) via Win32 message.", ver, resolvedSelector);
                }
            }
        }

        // Tier 3: Physical SendInput with cursor & focus restoration
        await EnsureUserIdleForPhysicalInputAsync();
        CheckCornerFailsafe();

        if (targetHwnd != IntPtr.Zero && Win32.GetWindowRect(targetHwnd, out var winRect))
        {
            OnOverlayUpdate?.Invoke(winRect);
        }

        try
        {
            usedTier = "input";
            Win32.SetCursorPos(screenX, screenY);
            await Task.Delay(15);

            SendMouseClickInput(button);
            if (isDouble)
            {
                await Task.Delay(25);
                SendMouseClickInput(button);
            }
            await Task.Delay(30);
        }
        finally
        {
            OnOverlayUpdate?.Invoke(null);
            Win32.SetCursorPos(origCursor.X, origCursor.Y);
            if (origFg != IntPtr.Zero && Win32.GetForegroundWindow() != origFg)
            {
                RestoreForegroundWindow(origFg);
            }
        }

        var verification = BuildVerification(usedTier, origFg, origCursor, beforeWins);
        return ($"Clicked @({screenX},{screenY}) ({button}{(isDouble ? ", double" : "")}) via {usedTier} (cursor & focus restored).", verification, resolvedSelector);
    }

    public async Task<(string Text, ActionVerificationDto Verification, object? ResolvedSelector)> TypeTextAsync(
        ElementEntry? element,
        int screenX,
        int screenY,
        IntPtr targetHwnd,
        string text,
        bool clearFirst = false,
        bool submit = false,
        bool viaClipboard = false,
        string mode = "auto",
        object? resolvedSelector = null)
    {
        CheckTargetWindowSecurity(targetHwnd);
        OnVisualAction?.Invoke(new VisualActionState
        {
            Type = "type",
            X = screenX,
            Y = screenY,
            Description = $"Yazılıyor: \"{(text.Length > 25 ? text[..25] + "..." : text)}\""
        });

        var beforeWins = _uia.EnumerateTopWindows();
        IntPtr origFg = Win32.GetForegroundWindow();
        Win32.GetCursorPos(out var origCursor);

        // Tier 1: UIA ValuePattern (instant, zero focus theft!)
        if ((mode is "auto" or "uia") && !viaClipboard && element?.Element != null)
        {
            bool uiaSet = await UiaEngine.RunMtaWithTimeoutAsync(() =>
            {
                var el = element.Element;
                if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var vpObj) && vpObj is ValuePattern vp && !vp.Current.IsReadOnly)
                {
                    string newVal = clearFirst ? text : (vp.Current.Value ?? "") + text;
                    vp.SetValue(newVal);
                    return true;
                }
                return false;
            }, 4000);

            if (uiaSet)
            {
                if (submit)
                {
                    IntPtr hwnd = element.NativeHwnd != IntPtr.Zero ? element.NativeHwnd : element.WindowHwnd;
                    SendEnterToHwnd(hwnd);
                }
                await Task.Delay(25);
                var ver = BuildVerification("uia", origFg, origCursor, beforeWins);
                return ($"Typed {text.Length} char(s) into [{element.Uid}] via UIA ValuePattern.", ver, resolvedSelector);
            }
        }

        // Tier 2: Background WM_CHAR / WM_SETTEXT to target Edit/Document HWND
        if ((mode is "auto" or "message") && !viaClipboard)
        {
            var (editHwnd, isKnownEdit) = FindWritableChildHwnd(element, targetHwnd, screenX, screenY);
            if (editHwnd != IntPtr.Zero && (isKnownEdit || mode == "message"))
            {
                if (clearFirst)
                {
                    Win32.SendMessageTimeout(editHwnd, Win32.WM_SETTEXT, IntPtr.Zero, text, Win32.SMTO_ABORTIFHUNG, 2000, out _);
                }
                else
                {
                    foreach (char ch in text)
                    {
                        if (ch == '\r') continue;
                        if (ch == '\n')
                        {
                            Win32.PostMessage(editHwnd, Win32.WM_KEYDOWN, (IntPtr)0x0D, IntPtr.Zero);
                            Win32.PostMessage(editHwnd, Win32.WM_KEYUP, (IntPtr)0x0D, IntPtr.Zero);
                        }
                        else
                        {
                            Win32.PostMessage(editHwnd, Win32.WM_CHAR, (IntPtr)ch, IntPtr.Zero);
                        }
                    }
                }
                if (submit)
                {
                    SendEnterToHwnd(editHwnd);
                }
                await Task.Delay(35);

                if (mode == "message" || isKnownEdit || VerifyWindowHasTextOrChanged(editHwnd))
                {
                    var ver = BuildVerification("message", origFg, origCursor, beforeWins);
                    return ($"Typed {text.Length} char(s) via background Win32 message.", ver, resolvedSelector);
                }
            }
            if (mode == "message")
            {
                throw new HelperErrorException("failed", "Hedef kontrol arka plan WM_CHAR / WM_SETTEXT mesajlarını kabul etmedi.");
            }
        }

        // Tier 3: Physical SendInput (KEYEVENTF_UNICODE or Clipboard paste) with focus & cursor restore
        await EnsureUserIdleForPhysicalInputAsync();
        CheckCornerFailsafe();

        IntPtr topHwnd = element?.WindowHwnd != IntPtr.Zero && element != null ? element.WindowHwnd : targetHwnd;
        if (topHwnd != IntPtr.Zero && Win32.GetWindowRect(topHwnd, out var winRect))
        {
            OnOverlayUpdate?.Invoke(winRect);
        }

        try
        {
            if (topHwnd != IntPtr.Zero)
            {
                BringWindowToFrontInternal(topHwnd);
                await Task.Delay(80);
            }

            if (element?.Element != null)
            {
                try { element.Element.SetFocus(); } catch { }
            }
            else if (screenX != 0 || screenY != 0)
            {
                Win32.SetCursorPos(screenX, screenY);
                SendMouseClickInput("left");
                await Task.Delay(50);
            }

            if (clearFirst)
            {
                SendKeyChord("ctrl+a");
                await Task.Delay(30);
                SendKeyChord("backspace");
                await Task.Delay(30);
            }

            if (viaClipboard)
            {
                var backup = _clipboard.BackupClipboard();
                try
                {
                    _clipboard.WriteText(text);
                    await Task.Delay(40);
                    SendKeyChord("ctrl+v");
                    await Task.Delay(80);
                }
                finally
                {
                    _clipboard.RestoreClipboard(backup);
                }
            }
            else
            {
                SendUnicodeStringInput(text);
            }

            if (submit)
            {
                await Task.Delay(30);
                SendKeyChord("enter");
            }

            await Task.Delay(100);
        }
        finally
        {
            ReleaseAllModifiers();
            OnOverlayUpdate?.Invoke(null);
            Win32.SetCursorPos(origCursor.X, origCursor.Y);
            if (origFg != IntPtr.Zero && origFg != topHwnd)
            {
                RestoreForegroundWindow(origFg);
            }
        }

        var verification = BuildVerification("input", origFg, origCursor, beforeWins);
        return ($"Typed {text.Length} char(s) via KEYEVENTF_UNICODE SendInput (focus & cursor restored).", verification, resolvedSelector);
    }

    public async Task<(string Text, ActionVerificationDto Verification)> PressKeyAsync(
        IEnumerable<string> chords,
        string? windowQuery,
        string? uid,
        int repeat = 1,
        string mode = "auto")
    {
        OnVisualAction?.Invoke(new VisualActionState
        {
            Type = "key",
            X = 0,
            Y = 0,
            Description = $"Klavye: {string.Join(" + ", chords)}"
        });

        IntPtr targetHwnd = IntPtr.Zero;
        if (!string.IsNullOrWhiteSpace(uid))
        {
            var el = await UiaEngine.RunMtaWithTimeoutAsync(() => _uia.ResolveElementByUid(uid), 4000);
            targetHwnd = el.NativeHwnd != IntPtr.Zero ? el.NativeHwnd : el.WindowHwnd;
        }
        else if (!string.IsNullOrWhiteSpace(windowQuery))
        {
            var win = _uia.ResolveWindow(windowQuery)
                ?? throw new HelperErrorException("not_found", $"Pencere bulunamadı: \"{windowQuery}\"");
            targetHwnd = win.Hwnd;
        }

        CheckTargetWindowSecurity(targetHwnd);
        var beforeWins = _uia.EnumerateTopWindows();
        IntPtr origFg = Win32.GetForegroundWindow();
        Win32.GetCursorPos(out var origCursor);

        await EnsureUserIdleForPhysicalInputAsync();
        CheckCornerFailsafe();

        try
        {
            if (targetHwnd != IntPtr.Zero && targetHwnd != origFg)
            {
                BringWindowToFrontInternal(targetHwnd);
                await Task.Delay(30);
            }

            for (int r = 0; r < repeat; r++)
            {
                foreach (var chord in chords)
                {
                    SendKeyChord(chord);
                    await Task.Delay(20);
                }
            }
            await Task.Delay(30);
        }
        finally
        {
            ReleaseAllModifiers();
            Win32.SetCursorPos(origCursor.X, origCursor.Y);
            if (origFg != IntPtr.Zero && Win32.IsWindow(origFg) && Win32.GetForegroundWindow() == targetHwnd && origFg != targetHwnd)
            {
                RestoreForegroundWindow(origFg);
            }
        }

        var ver = BuildVerification("input", origFg, origCursor, beforeWins);
        return ($"Pressed key(s): {string.Join(", ", chords)} (repeat={repeat}).", ver);
    }

    public async Task<(string Text, ActionVerificationDto Verification)> DragAsync(
        int fromX,
        int fromY,
        int toX,
        int toY,
        int durationMs = 350)
    {
        OnVisualAction?.Invoke(new VisualActionState
        {
            Type = "drag",
            FromX = fromX,
            FromY = fromY,
            ToX = toX,
            ToY = toY,
            X = toX,
            Y = toY,
            DurationMs = durationMs,
            Description = $"Sürükleniyor: ({fromX}, {fromY}) -> ({toX}, {toY})"
        });

        await EnsureUserIdleForPhysicalInputAsync();
        CheckCornerFailsafe();

        var beforeWins = _uia.EnumerateTopWindows();
        IntPtr origFg = Win32.GetForegroundWindow();
        Win32.GetCursorPos(out var origCursor);

        try
        {
            Win32.SetCursorPos(fromX, fromY);
            await Task.Delay(20);
            SendRawMouseFlag(Win32.MOUSEEVENTF_LEFTDOWN);
            await Task.Delay(20);

            int steps = Math.Max(8, durationMs / 20);
            for (int i = 1; i <= steps; i++)
            {
                CheckCornerFailsafe();
                double t = (double)i / steps;
                int cx = (int)Math.Round(fromX + (toX - fromX) * t);
                int cy = (int)Math.Round(fromY + (toY - fromY) * t);
                Win32.SetCursorPos(cx, cy);
                await Task.Delay(Math.Max(8, durationMs / steps));
            }

            SendRawMouseFlag(Win32.MOUSEEVENTF_LEFTUP);
            await Task.Delay(30);
        }
        finally
        {
            SendRawMouseFlag(Win32.MOUSEEVENTF_LEFTUP);
            Win32.SetCursorPos(origCursor.X, origCursor.Y);
            if (origFg != IntPtr.Zero && Win32.GetForegroundWindow() != origFg)
            {
                RestoreForegroundWindow(origFg);
            }
        }

        var ver = BuildVerification("input", origFg, origCursor, beforeWins);
        return ($"Dragged from ({fromX},{fromY}) to ({toX},{toY}) in {durationMs} ms.", ver);
    }

    public async Task<(string Text, ActionVerificationDto Verification)> ScrollAsync(
        ElementEntry? element,
        int screenX,
        int screenY,
        IntPtr targetHwnd,
        int dx = 0,
        int dy = 3,
        string? toEdge = null,
        string mode = "auto")
    {
        OnVisualAction?.Invoke(new VisualActionState
        {
            Type = "scroll",
            X = screenX,
            Y = screenY,
            Description = $"Kaydırma: {(dy > 0 ? "Aşağı" : "Yukarı")}"
        });

        var beforeWins = _uia.EnumerateTopWindows();
        IntPtr origFg = Win32.GetForegroundWindow();
        Win32.GetCursorPos(out var origCursor);

        // Tier 1: UIA ScrollPattern
        if ((mode is "auto" or "uia") && element?.Element != null)
        {
            bool scrolled = await UiaEngine.RunMtaWithTimeoutAsync(() =>
            {
                var el = element.Element;
                if (el.TryGetCurrentPattern(ScrollPattern.Pattern, out var spObj) && spObj is ScrollPattern sp)
                {
                    if (!string.IsNullOrEmpty(toEdge))
                    {
                        double hPct = toEdge == "left" ? 0 : toEdge == "right" ? 100 : ScrollPattern.NoScroll;
                        double vPct = toEdge == "top" ? 0 : toEdge == "bottom" ? 100 : ScrollPattern.NoScroll;
                        sp.SetScrollPercent(hPct, vPct);
                        return true;
                    }
                    var hAmt = dx > 0 ? ScrollAmount.SmallIncrement : dx < 0 ? ScrollAmount.SmallDecrement : ScrollAmount.NoAmount;
                    var vAmt = dy > 0 ? ScrollAmount.SmallIncrement : dy < 0 ? ScrollAmount.SmallDecrement : ScrollAmount.NoAmount;
                    sp.Scroll(hAmt, vAmt);
                    return true;
                }
                return false;
            }, 3000);

            if (scrolled)
            {
                var ver = BuildVerification("uia", origFg, origCursor, beforeWins);
                return ($"Scrolled [{element.Uid}] via UIA ScrollPattern.", ver);
            }
        }

        // Tier 2: WM_MOUSEWHEEL
        IntPtr hwnd = element?.NativeHwnd != IntPtr.Zero && element != null ? element.NativeHwnd : targetHwnd;
        if (hwnd == IntPtr.Zero) hwnd = Win32.WindowFromPoint(new Win32.POINT { X = screenX, Y = screenY });
        if (hwnd != IntPtr.Zero)
        {
            int wheelDelta = -dy * 120;
            IntPtr wParam = Win32.MakeLParam(0, wheelDelta);
            IntPtr lParam = Win32.MakeLParam(screenX, screenY);
            Win32.PostMessage(hwnd, Win32.WM_MOUSEWHEEL, wParam, lParam);
            await Task.Delay(25);
            var ver = BuildVerification("message", origFg, origCursor, beforeWins);
            return ($"Scrolled (dx={dx}, dy={dy}) via WM_MOUSEWHEEL.", ver);
        }

        throw new HelperErrorException("failed", "Kaydırılacak hedef pencere bulunamadı.");
    }

    public static void SendUnicodeStringInput(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var inputs = new List<Win32.INPUT>(text.Length * 2);
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '\r') continue;
            if (ch == '\n')
            {
                inputs.Add(CreateVkInput(0x0D, false));
                inputs.Add(CreateVkInput(0x0D, true));
                continue;
            }
            inputs.Add(new Win32.INPUT
            {
                type = Win32.INPUT_KEYBOARD,
                U = new Win32.InputUnion
                {
                    ki = new Win32.KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = ch,
                        dwFlags = Win32.KEYEVENTF_UNICODE
                    }
                }
            });
            inputs.Add(new Win32.INPUT
            {
                type = Win32.INPUT_KEYBOARD,
                U = new Win32.InputUnion
                {
                    ki = new Win32.KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = ch,
                        dwFlags = Win32.KEYEVENTF_UNICODE | Win32.KEYEVENTF_KEYUP
                    }
                }
            });
        }
        var arr = inputs.ToArray();
        Win32.SendInput((uint)arr.Length, arr, Marshal.SizeOf<Win32.INPUT>());
    }

    public static void SendKeyChord(string chord)
    {
        if (string.IsNullOrWhiteSpace(chord)) return;
        var parts = chord.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var modifiers = new List<ushort>();
        ushort mainVk = 0;

        foreach (var p in parts)
        {
            string lower = p.ToLowerInvariant();
            if (lower is "ctrl" or "control") modifiers.Add(0x11); // VK_CONTROL
            else if (lower is "alt" or "menu") modifiers.Add(0x12); // VK_MENU
            else if (lower is "shift") modifiers.Add(0x10); // VK_SHIFT
            else if (lower is "win" or "meta" or "super") modifiers.Add(0x5B); // VK_LWIN
            else mainVk = MapKeyNameToVk(lower);
        }

        var inputs = new List<Win32.INPUT>();
        try
        {
            foreach (var mod in modifiers)
                inputs.Add(CreateVkInput(mod, keyUp: false));

            if (mainVk != 0)
            {
                inputs.Add(CreateVkInput(mainVk, keyUp: false));
                inputs.Add(CreateVkInput(mainVk, keyUp: true));
            }
        }
        finally
        {
            for (int i = modifiers.Count - 1; i >= 0; i--)
                inputs.Add(CreateVkInput(modifiers[i], keyUp: true));

            if (inputs.Count > 0)
            {
                var arr = inputs.ToArray();
                Win32.SendInput((uint)arr.Length, arr, Marshal.SizeOf<Win32.INPUT>());
            }
        }
    }

    public static void ReleaseAllModifiers()
    {
        ushort[] modVks = { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 };
        var inputs = modVks.Select(vk => CreateVkInput(vk, keyUp: true)).ToArray();
        Win32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Win32.INPUT>());
    }

    public static void BringWindowToFrontInternal(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !Win32.IsWindow(hWnd)) return;
        if (Win32.IsIconic(hWnd))
        {
            Win32.ShowWindow(hWnd, Win32.SW_RESTORE);
        }
        IntPtr fg = Win32.GetForegroundWindow();
        if (fg == hWnd) return;

        uint fgThread = Win32.GetWindowThreadProcessId(fg, out _);
        uint curThread = Win32.GetCurrentThreadId();
        bool attached = false;
        if (fgThread != 0 && fgThread != curThread)
        {
            attached = Win32.AttachThreadInput(curThread, fgThread, true);
        }
        try
        {
            Win32.SetForegroundWindow(hWnd);
        }
        finally
        {
            if (attached) Win32.AttachThreadInput(curThread, fgThread, false);
        }
    }

    public static void RestoreForegroundWindow(IntPtr origFg)
    {
        if (origFg == IntPtr.Zero || !Win32.IsWindow(origFg)) return;
        BringWindowToFrontInternal(origFg);
    }

    private ActionVerificationDto BuildVerification(
        string via,
        IntPtr origFg,
        Win32.POINT origCursor,
        List<WindowEntry> beforeWindows)
    {
        Win32.GetCursorPos(out var curPt);
        IntPtr nowFg = Win32.GetForegroundWindow();
        var afterWindows = _uia.EnumerateTopWindows();

        var beforeWids = new HashSet<string>(beforeWindows.Select(w => w.Wid));
        var afterWids = new HashSet<string>(afterWindows.Select(w => w.Wid));

        var newWins = afterWindows
            .Where(w => !beforeWids.Contains(w.Wid))
            .Select(w => (object)new { wid = w.Wid, title = w.Title, app = w.ProcessName })
            .ToList();
        var closedWins = beforeWindows
            .Where(w => !afterWids.Contains(w.Wid))
            .Select(w => w.Wid)
            .ToList();

        return new ActionVerificationDto
        {
            Via = via,
            FocusRestored = origFg == IntPtr.Zero || nowFg == origFg,
            CursorRestored = Math.Abs(curPt.X - origCursor.X) <= 2 && Math.Abs(curPt.Y - origCursor.Y) <= 2,
            ActiveWindowChanged = nowFg != origFg,
            NewWindows = newWins,
            ClosedWindows = closedWins,
            Summary = $"Tier={via}, windows={afterWindows.Count}"
        };
    }

    private static (IntPtr Hwnd, bool IsKnownEditClass) FindWritableChildHwnd(ElementEntry? element, IntPtr targetHwnd, int screenX, int screenY)
    {
        if (element?.NativeHwnd != IntPtr.Zero && element != null) return (element.NativeHwnd, true);
        IntPtr root = element?.WindowHwnd != IntPtr.Zero && element != null ? element.WindowHwnd : targetHwnd;
        if (root == IntPtr.Zero) return (IntPtr.Zero, false);

        IntPtr editChild = IntPtr.Zero;
        Win32.EnumChildWindows(root, (child, _) =>
        {
            string cls = Win32.GetWindowClass(child);
            if (cls.Contains("Edit", StringComparison.OrdinalIgnoreCase) ||
                cls.Contains("RichEdit", StringComparison.OrdinalIgnoreCase) ||
                cls.Contains("Scintilla", StringComparison.OrdinalIgnoreCase))
            {
                editChild = child;
                return false;
            }
            return true;
        }, IntPtr.Zero);

        if (editChild != IntPtr.Zero) return (editChild, true);
        IntPtr ptWnd = Win32.WindowFromPoint(new Win32.POINT { X = screenX, Y = screenY });
        return (ptWnd != IntPtr.Zero ? ptWnd : root, false);
    }

    private static bool VerifyWindowHasTextOrChanged(IntPtr hwnd)
    {
        Win32.SendMessageTimeout(hwnd, Win32.WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero, Win32.SMTO_ABORTIFHUNG, 500, out IntPtr len);
        return len.ToInt64() > 0;
    }

    private static void SendEnterToHwnd(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        Win32.PostMessage(hwnd, Win32.WM_KEYDOWN, (IntPtr)0x0D, IntPtr.Zero);
        Win32.PostMessage(hwnd, Win32.WM_KEYUP, (IntPtr)0x0D, IntPtr.Zero);
    }

    private static void SendMouseClickInput(string button)
    {
        uint down = button == "right" ? Win32.MOUSEEVENTF_RIGHTDOWN : button == "middle" ? Win32.MOUSEEVENTF_MIDDLEDOWN : Win32.MOUSEEVENTF_LEFTDOWN;
        uint up = button == "right" ? Win32.MOUSEEVENTF_RIGHTUP : button == "middle" ? Win32.MOUSEEVENTF_MIDDLEUP : Win32.MOUSEEVENTF_LEFTUP;
        SendRawMouseFlag(down);
        SendRawMouseFlag(up);
    }

    private static void SendRawMouseFlag(uint flag)
    {
        var input = new Win32.INPUT
        {
            type = Win32.INPUT_MOUSE,
            U = new Win32.InputUnion
            {
                mi = new Win32.MOUSEINPUT { dwFlags = flag }
            }
        };
        Win32.SendInput(1, new[] { input }, Marshal.SizeOf<Win32.INPUT>());
    }

    private static Win32.INPUT CreateVkInput(ushort vk, bool keyUp)
    {
        uint flags = keyUp ? Win32.KEYEVENTF_KEYUP : 0;
        if (vk is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E or 0x5B or 0x5C)
        {
            flags |= Win32.KEYEVENTF_EXTENDEDKEY;
        }
        return new Win32.INPUT
        {
            type = Win32.INPUT_KEYBOARD,
            U = new Win32.InputUnion
            {
                ki = new Win32.KEYBDINPUT { wVk = vk, dwFlags = flags }
            }
        };
    }

    public static ushort MapKeyNameToVk(string key) => key switch
    {
        "enter" or "return" => 0x0D,
        "tab" => 0x09,
        "esc" or "escape" => 0x1B,
        "space" or "spacebar" => 0x20,
        "backspace" or "bksp" => 0x08,
        "delete" or "del" => 0x2E,
        "insert" or "ins" => 0x2D,
        "home" => 0x24,
        "end" => 0x23,
        "pageup" or "pgup" => 0x21,
        "pagedown" or "pgdn" => 0x22,
        "left" or "arrowleft" => 0x25,
        "up" or "arrowup" => 0x26,
        "right" or "arrowright" => 0x27,
        "down" or "arrowdown" => 0x28,
        "f1" => 0x70,
        "f2" => 0x71,
        "f3" => 0x72,
        "f4" => 0x73,
        "f5" => 0x74,
        "f6" => 0x75,
        "f7" => 0x76,
        "f8" => 0x77,
        "f9" => 0x78,
        "f10" => 0x79,
        "f11" => 0x7A,
        "f12" => 0x7B,
        "+" or "plus" => 0xBB,
        "-" or "minus" => 0xBD,
        "=" or "equals" => 0xBB,
        "*" or "multiply" => 0x6A,
        "/" or "divide" => 0x6F,
        _ when key.Length == 1 && char.IsLetterOrDigit(key[0]) => (ushort)char.ToUpperInvariant(key[0]),
        _ => 0
    };
}
