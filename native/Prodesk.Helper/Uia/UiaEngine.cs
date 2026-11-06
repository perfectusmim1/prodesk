using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using Prodesk.Helper.Native;

namespace Prodesk.Helper.Uia;

public class WindowEntry
{
    public string Wid { get; set; } = "";
    public IntPtr Hwnd { get; set; }
    public string Title { get; set; } = "";
    public string ProcessName { get; set; } = "";
    public uint Pid { get; set; }
    public string ClassName { get; set; } = "";
    public Win32.RECT Bounds { get; set; }
    public string State { get; set; } = "normal";
    public bool IsForeground { get; set; }
    public bool IsElevated { get; set; }
}

public class ElementEntry
{
    public string Uid { get; set; } = "";
    public string Wid { get; set; } = "";
    public IntPtr WindowHwnd { get; set; }
    public IntPtr NativeHwnd { get; set; }
    public int[]? RuntimeId { get; set; }
    public string Name { get; set; } = "";
    public string AutomationId { get; set; } = "";
    public string ControlTypeName { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string Value { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public bool IsOffscreen { get; set; }
    public bool IsPassword { get; set; }
    public bool HasKeyboardFocus { get; set; }
    public Win32.RECT Bounds { get; set; }
    public AutomationElement? Element { get; set; }
}

public class HelperErrorException : Exception
{
    public string ErrorCode { get; }

    public HelperErrorException(string errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }
}

public class UiaEngine
{
    private readonly object _mapLock = new();
    private readonly Dictionary<IntPtr, string> _hwndToWid = new();
    private readonly Dictionary<string, WindowEntry> _widToWindow = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ElementEntry> _uidMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _uidLru = new();
    private readonly ConcurrentDictionary<string, string[]> _lastWindowSnapshots = new(StringComparer.OrdinalIgnoreCase);
    private int _nextWindowIndex = 1;
    private int _nextElementIndex = 1;
    private const int MaxUidEntries = 5000;

    private static readonly HashSet<string> InteractiveRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Button", "Edit", "Document", "MenuItem", "MenuBar", "Menu", "ComboBox", "ListItem", "List",
        "CheckBox", "RadioButton", "Hyperlink", "TabItem", "Tab", "TreeItem", "Slider", "Spinner",
        "SplitButton", "ToolBar", "DataItem", "ScrollBar", "Window"
    };

    public static async Task<T> RunMtaWithTimeoutAsync<T>(Func<T> func, int timeoutMs = 8000)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                tcs.TrySetResult(func());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Prodesk-UIA-Worker"
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
        if (completed != tcs.Task)
        {
            throw new HelperErrorException("app_not_responding", $"Hedef uygulama {timeoutMs} ms içinde UIA isteğine yanıt vermedi.");
        }
        return await tcs.Task;
    }

    public List<WindowEntry> EnumerateTopWindows(bool includeMinimized = true, string? filter = null)
    {
        var list = new List<WindowEntry>();
        IntPtr fgHwnd = Win32.GetForegroundWindow();

        Win32.EnumWindows((hWnd, _) =>
        {
            if (!Win32.IsWindowVisible(hWnd)) return true;
            bool iconic = Win32.IsIconic(hWnd);
            if (iconic && !includeMinimized) return true;

            string title = Win32.GetWindowTitle(hWnd);
            string cls = Win32.GetWindowClass(hWnd);
            if (string.IsNullOrWhiteSpace(title) && cls is not ("Notepad" or "MSPaintApp")) return true;
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Windows.UI.Core.CoreWindow") return true;

            Win32.GetWindowRect(hWnd, out var rect);
            if (!iconic && (rect.Width <= 10 || rect.Height <= 10)) return true;

            Win32.GetWindowThreadProcessId(hWnd, out uint pid);
            string procName = GetSafeProcessName(pid);

            // Resolve real UWP process inside ApplicationFrameHost.exe (§3.2, §13.4)
            if (string.Equals(procName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
            {
                uint realPid = 0;
                Win32.EnumChildWindows(hWnd, (childHwnd, _) =>
                {
                    Win32.GetWindowThreadProcessId(childHwnd, out uint cPid);
                    if (cPid != 0 && cPid != pid)
                    {
                        realPid = cPid;
                        return false;
                    }
                    return true;
                }, IntPtr.Zero);
                if (realPid != 0)
                {
                    pid = realPid;
                    procName = GetSafeProcessName(realPid);
                }
            }

            if (!string.IsNullOrWhiteSpace(filter))
            {
                if (!title.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                    !procName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            string wid;
            lock (_mapLock)
            {
                if (!_hwndToWid.TryGetValue(hWnd, out wid!))
                {
                    wid = $"w{_nextWindowIndex++}";
                    _hwndToWid[hWnd] = wid;
                }
            }

            string state = iconic ? "minimized" : Win32.IsZoomed(hWnd) ? "maximized" : "normal";
            var entry = new WindowEntry
            {
                Wid = wid,
                Hwnd = hWnd,
                Title = title,
                ProcessName = procName,
                Pid = pid,
                ClassName = cls,
                Bounds = rect,
                State = state,
                IsForeground = hWnd == fgHwnd,
                IsElevated = Win32.IsProcessElevated(pid)
            };

            lock (_mapLock)
            {
                _widToWindow[wid] = entry;
            }
            list.Add(entry);
            return true;
        }, IntPtr.Zero);

        return list;
    }

    public WindowEntry? ResolveWindow(string? query)
    {
        var windows = EnumerateTopWindows(includeMinimized: true);
        if (string.IsNullOrWhiteSpace(query))
        {
            return windows.FirstOrDefault(w => w.IsForeground) ?? windows.FirstOrDefault();
        }
        string q = query.Trim();
        lock (_mapLock)
        {
            if (_widToWindow.TryGetValue(q, out var byWid) && Win32.IsWindow(byWid.Hwnd))
            {
                return byWid;
            }
        }
        string qNoExe = q.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? q[..^4] : q;
        return windows.FirstOrDefault(w => string.Equals(w.Wid, q, StringComparison.OrdinalIgnoreCase))
            ?? windows.FirstOrDefault(w => string.Equals(w.ProcessName, qNoExe, StringComparison.OrdinalIgnoreCase))
            ?? windows.FirstOrDefault(w => w.Title.Contains(q, StringComparison.OrdinalIgnoreCase))
            ?? windows.FirstOrDefault(w => w.ProcessName.Contains(qNoExe, StringComparison.OrdinalIgnoreCase));
    }

    public ElementEntry RegisterElement(string wid, IntPtr windowHwnd, AutomationElement el)
    {
        var current = el.Current;
        string role = NormalizeRoleName(current.ControlType);
        var bounds = ToWin32Rect(current.BoundingRectangle);
        string val = TryGetValueString(el);
        int[]? runtimeId = null;
        try { runtimeId = el.GetRuntimeId(); } catch { }

        lock (_mapLock)
        {
            // Check if same RuntimeId in same window already has a uid
            if (runtimeId != null && runtimeId.Length > 0)
            {
                foreach (var kv in _uidMap)
                {
                    if (kv.Value.Wid == wid && kv.Value.RuntimeId != null && kv.Value.RuntimeId.SequenceEqual(runtimeId))
                    {
                        kv.Value.Name = current.Name ?? "";
                        kv.Value.Value = val;
                        kv.Value.Bounds = bounds;
                        kv.Value.IsEnabled = current.IsEnabled;
                        kv.Value.HasKeyboardFocus = current.HasKeyboardFocus;
                        kv.Value.Element = el;
                        return kv.Value;
                    }
                }
            }

            string uid = $"{wid}:e{_nextElementIndex++}";
            var entry = new ElementEntry
            {
                Uid = uid,
                Wid = wid,
                WindowHwnd = windowHwnd,
                NativeHwnd = new IntPtr(current.NativeWindowHandle),
                RuntimeId = runtimeId,
                Name = current.Name ?? "",
                AutomationId = current.AutomationId ?? "",
                ControlTypeName = role,
                ClassName = current.ClassName ?? "",
                Value = val,
                IsEnabled = current.IsEnabled,
                IsOffscreen = current.IsOffscreen,
                IsPassword = current.IsPassword,
                HasKeyboardFocus = current.HasKeyboardFocus,
                Bounds = bounds,
                Element = el
            };

            _uidMap[uid] = entry;
            _uidLru.AddLast(uid);
            while (_uidMap.Count > MaxUidEntries && _uidLru.First != null)
            {
                string oldest = _uidLru.First.Value;
                _uidLru.RemoveFirst();
                _uidMap.Remove(oldest);
            }
            return entry;
        }
    }

    public ElementEntry ResolveElementByUid(string uid)
    {
        ElementEntry? cached;
        lock (_mapLock)
        {
            _uidMap.TryGetValue(uid, out cached);
        }
        if (cached == null)
        {
            throw new HelperErrorException("not_found", $"Öğe uid=\"{uid}\" bulunamadı. Lütfen önce snapshot çağırın.");
        }

        // Verify cached element is still alive
        if (cached.Element != null)
        {
            try
            {
                _ = cached.Element.Current.ControlType;
                cached.Bounds = ToWin32Rect(cached.Element.Current.BoundingRectangle);
                cached.Value = TryGetValueString(cached.Element);
                return cached;
            }
            catch
            {
                // Stale COM reference — re-resolve below using AutomationId/Name/ControlType (§3.2)
            }
        }

        if (!Win32.IsWindow(cached.WindowHwnd))
        {
            throw new HelperErrorException("stale_element", $"Öğe uid=\"{uid}\" ait olduğu pencere kapandığı için geçersiz (stale_element).");
        }

        try
        {
            var root = AutomationElement.FromHandle(cached.WindowHwnd);
            var conditions = new List<Condition>();
            if (!string.IsNullOrEmpty(cached.AutomationId))
                conditions.Add(new PropertyCondition(AutomationElement.AutomationIdProperty, cached.AutomationId));
            if (!string.IsNullOrEmpty(cached.Name))
                conditions.Add(new PropertyCondition(AutomationElement.NameProperty, cached.Name));

            Condition searchCond = conditions.Count switch
            {
                0 => Condition.TrueCondition,
                1 => conditions[0],
                _ => new AndCondition(conditions.ToArray())
            };

            var found = root.FindFirst(TreeScope.Descendants, searchCond);
            if (found != null)
            {
                cached.Element = found;
                cached.Bounds = ToWin32Rect(found.Current.BoundingRectangle);
                cached.NativeHwnd = new IntPtr(found.Current.NativeWindowHandle);
                return cached;
            }
        }
        catch
        {
            // ignore
        }

        throw new HelperErrorException("stale_element", $"Öğe uid=\"{uid}\" artık arayüzde mevcut değil (stale_element). Yeniden snapshot alın.");
    }

    public (string Text, object Data) BuildSnapshot(
        string? windowQuery,
        bool interactiveOnly = true,
        int maxDepth = 10,
        int maxChars = 30000,
        bool diff = false,
        string? rootUid = null,
        bool includeOffscreen = false)
    {
        List<WindowEntry> targetWindows;
        if (!string.IsNullOrWhiteSpace(windowQuery))
        {
            var win = ResolveWindow(windowQuery)
                ?? throw new HelperErrorException("not_found", $"Pencere bulunamadı: \"{windowQuery}\"");
            targetWindows = new List<WindowEntry> { win };
        }
        else
        {
            targetWindows = EnumerateTopWindows(includeMinimized: false).Take(8).ToList();
        }

        var sb = new StringBuilder();
        var allElements = new List<object>();
        bool anyBrowserWindow = false;

        foreach (var win in targetWindows)
        {
            if (win.ProcessName.Equals("msedge", StringComparison.OrdinalIgnoreCase) ||
                win.ProcessName.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
                win.ProcessName.Equals("brave", StringComparison.OrdinalIgnoreCase) ||
                win.ProcessName.Equals("firefox", StringComparison.OrdinalIgnoreCase))
            {
                anyBrowserWindow = true;
            }

            var winLines = new List<string>();
            string fgTag = win.IsForeground ? " (foreground)" : "";
            string elevTag = win.IsElevated ? " (elevated)" : "";
            winLines.Add($"Window \"{win.Title}\" [{win.Wid}] pid={win.Pid} app={win.ProcessName}.exe{fgTag}{elevTag} @{win.Bounds.Left},{win.Bounds.Top} {win.Bounds.Width}x{win.Bounds.Height}");

            try
            {
                AutomationElement rootEl = !string.IsNullOrWhiteSpace(rootUid)
                    ? ResolveElementByUid(rootUid).Element!
                    : AutomationElement.FromHandle(win.Hwnd);

                int effectiveMaxDepth = anyBrowserWindow ? Math.Min(maxDepth, 5) : maxDepth;
                WalkTree(win.Wid, win.Hwnd, rootEl, 1, effectiveMaxDepth, interactiveOnly, includeOffscreen, winLines, allElements);
            }
            catch (Exception ex)
            {
                winLines.Add($"  [UIA walk warning: {ex.Message}]");
            }

            string[] currentArr = winLines.ToArray();
            if (diff && _lastWindowSnapshots.TryGetValue(win.Wid, out var prevArr))
            {
                var prevSet = new HashSet<string>(prevArr);
                var diffLines = currentArr.Where((l, idx) => idx == 0 || !prevSet.Contains(l)).ToList();
                if (diffLines.Count == 1)
                {
                    diffLines.Add("  (no UI changes since previous snapshot)");
                }
                foreach (var l in diffLines) sb.AppendLine(l);
            }
            else
            {
                foreach (var l in currentArr) sb.AppendLine(l);
            }

            _lastWindowSnapshots[win.Wid] = currentArr;
        }

        if (anyBrowserWindow)
        {
            sb.AppendLine();
            sb.AppendLine("[Not: Hedef bir web tarayıcısıdır. Web sayfası DOM içeriğiyle etkileşim için Prowser MCP (new_window) araçlarını kullanın; Prodesk'i yalnızca yerel dosya/yazdırma diyalogları veya pencere yönetimi için kullanın.]");
        }

        string output = sb.ToString().TrimEnd();
        if (output.Length > maxChars)
        {
            output = output[..maxChars] + $"\n... [truncated at {maxChars} chars]";
        }

        return (output, new
        {
            windowCount = targetWindows.Count,
            elementCount = allElements.Count,
            windows = targetWindows.Select(w => new { w.Wid, w.Title, w.ProcessName, w.Pid, w.State, w.IsForeground, w.IsElevated })
        });
    }

    private void WalkTree(
        string wid,
        IntPtr windowHwnd,
        AutomationElement parent,
        int depth,
        int maxDepth,
        bool interactiveOnly,
        bool includeOffscreen,
        List<string> lines,
        List<object> elementsOut)
    {
        if (depth > maxDepth || elementsOut.Count >= 400) return;

        var walker = TreeWalker.ControlViewWalker;
        AutomationElement? child;
        try
        {
            child = walker.GetFirstChild(parent);
        }
        catch
        {
            return;
        }

        while (child != null && elementsOut.Count < 400)
        {
            try
            {
                var cur = child.Current;
                string role = NormalizeRoleName(cur.ControlType);
                bool offscreen = cur.IsOffscreen;
                if (!offscreen || includeOffscreen)
                {
                    bool isInteractive = InteractiveRoles.Contains(role) || !string.IsNullOrWhiteSpace(cur.Name) || !string.IsNullOrWhiteSpace(cur.AutomationId);
                    if (!interactiveOnly || isInteractive)
                    {
                        var entry = RegisterElement(wid, windowHwnd, child);
                        string indent = new(' ', depth * 2);
                        string namePart = !string.IsNullOrEmpty(entry.Name) ? $" \"{Truncate(entry.Name, 80)}\"" : "";
                        string idPart = !string.IsNullOrEmpty(entry.AutomationId) ? $" id=\"{entry.AutomationId}\"" : "";
                        var flags = new List<string>();
                        if (!entry.IsEnabled) flags.Add("disabled");
                        if (entry.HasKeyboardFocus) flags.Add("focused");
                        if (entry.IsPassword) flags.Add("password");
                        string flagPart = flags.Count > 0 ? $" ({string.Join(", ", flags)})" : "";
                        string valPart = !string.IsNullOrEmpty(entry.Value) && !entry.IsPassword
                            ? $" value=\"{Truncate(entry.Value, 60)}\""
                            : "";
                        string boundsPart = entry.Bounds.Width > 0 && entry.Bounds.Height > 0
                            ? $" @{entry.Bounds.Left},{entry.Bounds.Top} {entry.Bounds.Width}x{entry.Bounds.Height}"
                            : "";

                        lines.Add($"{indent}{role}{namePart} [{entry.Uid}]{idPart}{flagPart}{valPart}{boundsPart}");
                        elementsOut.Add(new
                        {
                            uid = entry.Uid,
                            role = entry.ControlTypeName,
                            name = entry.Name,
                            automationId = entry.AutomationId,
                            bounds = new { x = entry.Bounds.Left, y = entry.Bounds.Top, width = entry.Bounds.Width, height = entry.Bounds.Height }
                        });
                    }
                }

                WalkTree(wid, windowHwnd, child, depth + 1, maxDepth, interactiveOnly, includeOffscreen, lines, elementsOut);
            }
            catch
            {
                // skip inaccessible node
            }

            try
            {
                child = walker.GetNextSibling(child);
            }
            catch
            {
                break;
            }
        }
    }

    public List<ElementEntry> FindElements(
        string? windowQuery,
        string? name,
        string? role,
        string? automationId,
        string? className,
        bool exact = false,
        int maxResults = 25)
    {
        List<WindowEntry> targetWindows;
        if (!string.IsNullOrWhiteSpace(windowQuery))
        {
            var win = ResolveWindow(windowQuery)
                ?? throw new HelperErrorException("not_found", $"Pencere bulunamadı: \"{windowQuery}\"");
            targetWindows = new List<WindowEntry> { win };
        }
        else
        {
            targetWindows = EnumerateTopWindows(includeMinimized: true);
        }

        var results = new List<ElementEntry>();

        foreach (var win in targetWindows)
        {
            if (results.Count >= maxResults) break;
            AutomationElement root;
            try
            {
                root = AutomationElement.FromHandle(win.Hwnd);
            }
            catch
            {
                continue;
            }

            void SearchNode(AutomationElement node, int depth)
            {
                if (depth > 14 || results.Count >= maxResults) return;
                var walker = TreeWalker.ControlViewWalker;
                AutomationElement? child;
                try { child = walker.GetFirstChild(node); } catch { return; }

                while (child != null && results.Count < maxResults)
                {
                    try
                    {
                        var cur = child.Current;
                        string cRole = NormalizeRoleName(cur.ControlType);
                        string cName = cur.Name ?? "";
                        string cId = cur.AutomationId ?? "";
                        string cClass = cur.ClassName ?? "";

                        bool match = true;
                        if (!string.IsNullOrEmpty(role) && !cRole.Equals(role, StringComparison.OrdinalIgnoreCase))
                            match = false;
                        if (match && !string.IsNullOrEmpty(automationId) && !cId.Equals(automationId, StringComparison.OrdinalIgnoreCase))
                            match = false;
                        if (match && !string.IsNullOrEmpty(className) && !cClass.Contains(className, StringComparison.OrdinalIgnoreCase))
                            match = false;
                        if (match && !string.IsNullOrEmpty(name))
                        {
                            match = exact
                                ? cName.Equals(name, StringComparison.OrdinalIgnoreCase)
                                : cName.Contains(name, StringComparison.OrdinalIgnoreCase);
                        }

                        if (match)
                        {
                            results.Add(RegisterElement(win.Wid, win.Hwnd, child));
                        }
                        SearchNode(child, depth + 1);
                    }
                    catch { }

                    try { child = walker.GetNextSibling(child); } catch { break; }
                }
            }

            SearchNode(root, 1);
        }

        return results;
    }

    public ElementEntry GetElementAtPoint(int screenX, int screenY)
    {
        var el = AutomationElement.FromPoint(new System.Windows.Point(screenX, screenY));
        IntPtr hwnd = Win32.WindowFromPoint(new Win32.POINT { X = screenX, Y = screenY });
        var topWin = EnumerateTopWindows().FirstOrDefault(w => w.Hwnd == hwnd) ?? EnumerateTopWindows().FirstOrDefault();
        string wid = topWin?.Wid ?? "w0";
        IntPtr topHwnd = topWin?.Hwnd ?? hwnd;
        return RegisterElement(wid, topHwnd, el);
    }

    public List<ElementEntry> GetRecentWindowInteractiveElements(IntPtr windowHwnd)
    {
        lock (_mapLock)
        {
            return _uidMap.Values
                .Where(e => e.WindowHwnd == windowHwnd && !e.IsOffscreen && e.Bounds.Width > 4 && e.Bounds.Height > 4)
                .Take(60)
                .ToList();
        }
    }

    public static List<string> GetSupportedPatterns(AutomationElement el)
    {
        var list = new List<string>();
        try
        {
            foreach (var p in el.GetSupportedPatterns())
            {
                string n = Automation.PatternName(p);
                if (!string.IsNullOrEmpty(n)) list.Add(n);
            }
        }
        catch { }
        return list;
    }

    public static string TryGetValueString(AutomationElement el)
    {
        try
        {
            if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var vpObj) && vpObj is ValuePattern vp)
            {
                return vp.Current.Value ?? "";
            }
            if (el.TryGetCurrentPattern(RangeValuePattern.Pattern, out var rvpObj) && rvpObj is RangeValuePattern rvp)
            {
                return rvp.Current.Value.ToString();
            }
            if (el.TryGetCurrentPattern(TogglePattern.Pattern, out var tpObj) && tpObj is TogglePattern tp)
            {
                return tp.Current.ToggleState.ToString();
            }
            if (el.TryGetCurrentPattern(TextPattern.Pattern, out var txtObj) && txtObj is TextPattern txt)
            {
                return txt.DocumentRange.GetText(200)?.Trim() ?? "";
            }
        }
        catch { }
        return "";
    }

    private static string NormalizeRoleName(ControlType? ct)
    {
        if (ct == null) return "Custom";
        string prog = ct.ProgrammaticName ?? "ControlType.Custom";
        int dot = prog.IndexOf('.');
        return dot >= 0 ? prog[(dot + 1)..] : prog;
    }

    private static Win32.RECT ToWin32Rect(System.Windows.Rect r)
    {
        if (r.IsEmpty || double.IsInfinity(r.Left) || double.IsNaN(r.Left))
            return new Win32.RECT();
        return new Win32.RECT
        {
            Left = (int)Math.Round(r.Left),
            Top = (int)Math.Round(r.Top),
            Right = (int)Math.Round(r.Right),
            Bottom = (int)Math.Round(r.Bottom)
        };
    }

    private static string GetSafeProcessName(uint pid)
    {
        if (pid == 0) return "unknown";
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch
        {
            return "unknown";
        }
    }

    private static string Truncate(string s, int max)
    {
        string clean = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return clean.Length <= max ? clean : clean[..max] + "...";
    }
}
