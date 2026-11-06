using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Prodesk.Helper.Input;
using Prodesk.Helper.Native;
using Prodesk.Helper.Ui;
using Prodesk.Helper.Uia;

namespace Prodesk.Helper.Windows;

[ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IApplicationActivationManager
{
    int ActivateApplication(
        [In, MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
        [In, MarshalAs(UnmanagedType.LPWStr)] string? arguments,
        [In] uint options,
        [Out] out uint processId);
}

[ComImport, Guid("45ba127d-10a8-46ea-8ab7-56ea9078943c")]
internal class ApplicationActivationManager { }

public class WindowAndAppManager
{
    private readonly UiaEngine _uia;
    private readonly HelperUiManager? _ui;

    public WindowAndAppManager(UiaEngine uia, HelperUiManager? ui = null)
    {
        _uia = uia;
        _ui = ui;
    }

    public async Task<(string Text, object Data)> LaunchAppAsync(
        string? path,
        string? name,
        string? aumid,
        string[]? args,
        string? cwd,
        bool background = true,
        bool waitForWindow = true,
        int timeoutMs = 8000)
    {
        IntPtr origFg = Win32.GetForegroundWindow();
        var beforeWids = new HashSet<string>(_uia.EnumerateTopWindows().Select(w => w.Wid));

        string targetExe;
        string argLine = args != null && args.Length > 0
            ? string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))
            : "";

        Process? proc = null;
        uint? launchedPid = null;

        if (!string.IsNullOrWhiteSpace(aumid))
        {
            targetExe = aumid.Trim();
            try
            {
                var aam = (IApplicationActivationManager)new ApplicationActivationManager();
                aam.ActivateApplication(targetExe, string.IsNullOrEmpty(argLine) ? null : argLine, 0, out uint aumidPid);
                if (aumidPid != 0) launchedPid = aumidPid;
            }
            catch
            {
                var shellPsi = new ProcessStartInfo
                {
                    FileName = $"shell:AppsFolder\\{targetExe}",
                    UseShellExecute = true
                };
                proc = Process.Start(shellPsi);
                if (proc != null) launchedPid = (uint)proc.Id;
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                targetExe = Environment.ExpandEnvironmentVariables(path.Trim());
            }
            else if (!string.IsNullOrWhiteSpace(name))
            {
                targetExe = ResolveFriendlyAppName(name.Trim());
            }
            else
            {
                throw new HelperErrorException("bad_request", "launch_app için path, name veya aumid belirtilmelidir.");
            }

            var psi = new ProcessStartInfo
            {
                FileName = targetExe,
                Arguments = argLine,
                UseShellExecute = true,
                WindowStyle = background ? ProcessWindowStyle.Minimized : ProcessWindowStyle.Normal
            };
            if (!string.IsNullOrWhiteSpace(cwd))
            {
                psi.WorkingDirectory = Environment.ExpandEnvironmentVariables(cwd);
            }

            try
            {
                proc = Process.Start(psi);
                if (proc != null) launchedPid = (uint)proc.Id;
            }
            catch (Exception ex)
            {
                throw new HelperErrorException("failed", $"Uygulama başlatılamadı (\"{targetExe}\"): {ex.Message}");
            }
        }

        WindowEntry? launchedWin = null;
        bool isCalcLaunch = (!string.IsNullOrWhiteSpace(aumid) && aumid.Contains("WindowsCalculator", StringComparison.OrdinalIgnoreCase)) ||
                            string.Equals(targetExe, "calc.exe", StringComparison.OrdinalIgnoreCase);

        if (waitForWindow)
        {
            int effectiveWait = isCalcLaunch ? Math.Min(timeoutMs, 1600) : timeoutMs;
            int elapsed = 0;
            while (elapsed < effectiveWait)
            {
                await Task.Delay(200);
                elapsed += 200;
                var currentWins = _uia.EnumerateTopWindows(includeMinimized: true);
                launchedWin = currentWins.FirstOrDefault(w => !beforeWids.Contains(w.Wid))
                    ?? (launchedPid.HasValue ? currentWins.FirstOrDefault(w => w.Pid == launchedPid.Value) : null);

                if (launchedWin != null)
                {
                    if (!string.IsNullOrWhiteSpace(aumid))
                    {
                        // Give UWP XAML CoreWindow a brief moment to finish populating UIA tree before restoring background focus
                        await Task.Delay(350);
                        currentWins = _uia.EnumerateTopWindows(includeMinimized: true);
                        launchedWin = currentWins.FirstOrDefault(w => w.Hwnd == launchedWin.Hwnd) ?? launchedWin;
                    }
                    // Restore to non-minimized normal bounds without stealing focus (SW_SHOWNOACTIVATE) so UIA & PrintWindow work in background!
                    if (background && Win32.IsIconic(launchedWin.Hwnd))
                    {
                        Win32.ShowWindow(launchedWin.Hwnd, Win32.SW_SHOWNOACTIVATE);
                    }
                    if (background && origFg != IntPtr.Zero && Win32.GetForegroundWindow() != origFg)
                    {
                        ActionEngine.RestoreForegroundWindow(origFg);
                    }
                    break;
                }
            }

            if (launchedWin == null && isCalcLaunch && _ui != null)
            {
                IntPtr calcHwnd = _ui.ShowOrGetNativeCalculatorWindow();
                await Task.Delay(150);
                var currentWins = _uia.EnumerateTopWindows(includeMinimized: true);
                launchedWin = currentWins.FirstOrDefault(w => w.Hwnd == calcHwnd)
                    ?? currentWins.FirstOrDefault(w => !beforeWids.Contains(w.Wid));
            }
        }

        if (background && origFg != IntPtr.Zero && Win32.GetForegroundWindow() != origFg)
        {
            ActionEngine.RestoreForegroundWindow(origFg);
        }

        return (
            launchedWin != null
                ? $"Launched {launchedWin.ProcessName} in background -> window [{launchedWin.Wid}] \"{launchedWin.Title}\" (pid={launchedWin.Pid})."
                : $"Launched \"{targetExe}\" (pid={proc?.Id}).",
            new
            {
                wid = launchedWin?.Wid,
                title = launchedWin?.Title,
                pid = launchedWin?.Pid ?? (uint?)proc?.Id,
                processName = launchedWin?.ProcessName,
                background,
                focusPreserved = origFg == IntPtr.Zero || Win32.GetForegroundWindow() == origFg
            }
        );
    }

    public (string Text, object Data) PerformWindowAction(
        string windowQuery,
        string action,
        int? x,
        int? y,
        int? width,
        int? height)
    {
        var win = _uia.ResolveWindow(windowQuery)
            ?? throw new HelperErrorException("not_found", $"Pencere bulunamadı: \"{windowQuery}\"");

        IntPtr h = win.Hwnd;
        switch (action.ToLowerInvariant())
        {
            case "minimize":
                Win32.ShowWindow(h, Win32.SW_MINIMIZE);
                break;
            case "maximize":
                Win32.ShowWindow(h, Win32.SW_MAXIMIZE);
                break;
            case "restore":
                Win32.ShowWindow(h, Win32.SW_RESTORE);
                break;
            case "restore_noactivate":
                Win32.ShowWindow(h, Win32.SW_SHOWNOACTIVATE);
                break;
            case "close":
                Win32.PostMessage(h, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                break;
            case "move":
                Win32.GetWindowRect(h, out var rMove);
                Win32.SetWindowPos(
                    h, IntPtr.Zero,
                    x ?? rMove.Left, y ?? rMove.Top,
                    width ?? rMove.Width, height ?? rMove.Height,
                    Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
                break;
            case "resize":
                Win32.GetWindowRect(h, out var rSize);
                Win32.SetWindowPos(
                    h, IntPtr.Zero,
                    x ?? rSize.Left, y ?? rSize.Top,
                    width ?? rSize.Width, height ?? rSize.Height,
                    Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
                break;
            case "topmost":
                Win32.SetWindowPos(h, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
                break;
            case "untopmost":
                Win32.SetWindowPos(h, Win32.HWND_NOTOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
                break;
            default:
                throw new HelperErrorException("bad_request", $"Geçersiz pencere eylemi: {action}");
        }

        return ($"Window [{win.Wid}] \"{win.Title}\" -> {action}", new { wid = win.Wid, title = win.Title, action });
    }

    public async Task<(string Text, object Data)> CloseAppAsync(
        string? windowQuery,
        string? processName,
        int? pid,
        bool force = false)
    {
        if (pid.HasValue)
        {
            try
            {
                using var p = Process.GetProcessById(pid.Value);
                if (!force && p.MainWindowHandle != IntPtr.Zero)
                {
                    Win32.PostMessage(p.MainWindowHandle, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    await Task.Delay(500);
                }
                if (!p.HasExited && force)
                {
                    p.Kill(entireProcessTree: true);
                }
                return ($"Closed process pid={pid.Value}.", new { pid = pid.Value, force });
            }
            catch (Exception ex)
            {
                throw new HelperErrorException("not_found", $"Süreç kapatılamadı (pid={pid.Value}): {ex.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(windowQuery))
        {
            var win = _uia.ResolveWindow(windowQuery);
            if (win != null)
            {
                Win32.PostMessage(win.Hwnd, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                await Task.Delay(400);
                if (force && Win32.IsWindow(win.Hwnd))
                {
                    try
                    {
                        using var p = Process.GetProcessById((int)win.Pid);
                        p.Kill(entireProcessTree: true);
                    }
                    catch { }
                }
                return ($"Closed window [{win.Wid}] \"{win.Title}\".", new { wid = win.Wid, pid = win.Pid });
            }
        }

        if (!string.IsNullOrWhiteSpace(processName))
        {
            string clean = processName.Trim();
            if (clean.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) clean = clean[..^4];
            var procs = Process.GetProcessesByName(clean);
            int closed = 0;
            if (clean.Contains("Calculator", StringComparison.OrdinalIgnoreCase) && _ui != null)
            {
                if (_ui.CloseNativeCalculatorWindow()) closed++;
            }
            foreach (var p in procs)
            {
                try
                {
                    if (!force && p.MainWindowHandle != IntPtr.Zero)
                    {
                        Win32.PostMessage(p.MainWindowHandle, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    }
                    else
                    {
                        p.Kill(entireProcessTree: true);
                    }
                    closed++;
                }
                catch { }
                finally { p.Dispose(); }
            }
            return ($"Closed {closed} instance(s) of {clean}.", new { process = clean, closed });
        }

        throw new HelperErrorException("bad_request", "close_app için window, process veya pid belirtin.");
    }

    public List<object> ListInstalledApps(string? filter = null)
    {
        var apps = new List<(string Name, string Source, string Target)>();

        // 1. Start Menu shortcuts
        string[] startMenus =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)
        };
        foreach (var sm in startMenus)
        {
            if (!Directory.Exists(sm)) continue;
            try
            {
                foreach (var lnk in Directory.EnumerateFiles(sm, "*.lnk", SearchOption.AllDirectories))
                {
                    string title = Path.GetFileNameWithoutExtension(lnk);
                    apps.Add((title, "StartMenu", lnk));
                }
            }
            catch { }
        }

        // 2. Registry Uninstall entries
        string[] regPaths =
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var sub in regPaths)
            {
                try
                {
                    using var key = root.OpenSubKey(sub);
                    if (key == null) continue;
                    foreach (var skName in key.GetSubKeyNames())
                    {
                        using var sk = key.OpenSubKey(skName);
                        string? displayName = sk?.GetValue("DisplayName") as string;
                        string? installLoc = sk?.GetValue("InstallLocation") as string ?? sk?.GetValue("DisplayIcon") as string;
                        if (!string.IsNullOrWhiteSpace(displayName))
                        {
                            apps.Add((displayName.Trim(), "Registry", installLoc ?? ""));
                        }
                    }
                }
                catch { }
            }
        }

        var distinct = apps
            .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Where(a => string.IsNullOrWhiteSpace(filter) || a.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.Name)
            .Take(150)
            .Select(a => (object)new { name = a.Name, source = a.Source, target = a.Target })
            .ToList();

        return distinct;
    }

    private static string ResolveFriendlyAppName(string name)
    {
        string lower = name.ToLowerInvariant();
        if (lower is "not defteri" or "notepad") return "notepad.exe";
        if (lower is "hesap makinesi" or "calculator" or "calc") return "calc.exe";
        if (lower is "paint" or "mspaint") return "mspaint.exe";
        if (lower is "gezgin" or "dosya gezgini" or "explorer") return "explorer.exe";

        string[] startMenus =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)
        };
        foreach (var sm in startMenus)
        {
            if (!Directory.Exists(sm)) continue;
            try
            {
                var hit = Directory.EnumerateFiles(sm, "*.lnk", SearchOption.AllDirectories)
                    .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Contains(name, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit;
            }
            catch { }
        }
        return name;
    }
}
