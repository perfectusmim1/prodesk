using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.VisualBasic.FileIO;
using Prodesk.Helper.Capture;
using Prodesk.Helper.Native;
using Prodesk.Helper.Uia;

namespace Prodesk.Helper.Install;

public static class SystemAndVerifyService
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, ref WINTRUST_DATA pWVTData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private class MEMORYSTATUSEX
    {
        public uint dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

    public static (string Text, object Data) VerifyFileSignature(string rawPath)
    {
        string fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(rawPath));
        if (!File.Exists(fullPath))
        {
            throw new HelperErrorException("not_found", $"Dosya bulunamadı: {fullPath}");
        }

        string sha256;
        using (var fs = File.OpenRead(fullPath))
        {
            sha256 = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        }

        // Check WinVerifyTrust Authenticode status
        bool isTrusted = false;
        int trustStatus;
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = fullPath
        };
        IntPtr pFileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFileInfo, false);
            var wtData = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2, // WTD_UI_NONE
                fdwRevocationChecks = 0, // WTD_REVOKE_NONE
                dwUnionChoice = 1, // WTD_CHOICE_FILE
                pFile = pFileInfo,
                dwStateAction = 0,
                dwProvFlags = 0
            };
            var actionGuid = new Guid("{00AAC56B-CD44-11d0-8CC2-00C04FC295EE}"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            trustStatus = WinVerifyTrust(IntPtr.Zero, actionGuid, ref wtData);
            isTrusted = trustStatus == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(pFileInfo);
        }

        string? subject = null;
        string? issuer = null;
        string? thumbprint = null;
        bool hasCert = false;
        try
        {
            using var cert = X509Certificate.CreateFromSignedFile(fullPath);
            using var cert2 = new X509Certificate2(cert);
            hasCert = true;
            subject = cert2.Subject;
            issuer = cert2.Issuer;
            thumbprint = cert2.Thumbprint;
        }
        catch
        {
            // unsigned file
        }

        bool isSigned = isTrusted || hasCert;
        var data = new
        {
            path = fullPath,
            sha256,
            isSigned,
            isTrustedAuthenticode = isTrusted,
            winVerifyTrustCode = trustStatus,
            subject,
            issuer,
            thumbprint
        };

        string statusLabel = isTrusted ? "Signed & Trusted (Authenticode OK)"
            : hasCert ? "Signed (Certificate present, untrusted chain)"
            : "Unsigned";

        return ($"File: {fullPath}\nStatus: {statusLabel}\nSigner: {subject ?? "none"}\nSHA256: {sha256}", data);
    }

    public static (string Text, object Data) MoveToRecycleBin(string rawPath)
    {
        string full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(rawPath));
        if (File.Exists(full))
        {
            FileSystem.DeleteFile(full, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            return ($"Moved file to Recycle Bin: {full}", new { path = full, recycled = true });
        }
        if (Directory.Exists(full))
        {
            FileSystem.DeleteDirectory(full, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            return ($"Moved directory to Recycle Bin: {full}", new { path = full, recycled = true });
        }
        throw new HelperErrorException("not_found", $"Dosya veya klasör bulunamadı: {full}");
    }

    public static (string Text, object Data) ListProcesses(string? filter = null, int maxResults = 100)
    {
        var list = new List<object>();
        var lines = new List<string>();

        foreach (var p in Process.GetProcesses().OrderByDescending(pr =>
        {
            try { return pr.WorkingSet64; } catch { return 0L; }
        }))
        {
            try
            {
                if (list.Count >= maxResults) break;
                string name = p.ProcessName;
                string title = "";
                try { title = p.MainWindowTitle ?? ""; } catch { }

                if (!string.IsNullOrWhiteSpace(filter))
                {
                    if (!name.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                        !title.Contains(filter, StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                long memMb = 0;
                try { memMb = p.WorkingSet64 / (1024 * 1024); } catch { }
                bool elevated = Win32.IsProcessElevated((uint)p.Id);

                list.Add(new
                {
                    pid = p.Id,
                    name,
                    memoryMb = memMb,
                    mainWindowTitle = title,
                    elevated
                });
                lines.Add($"PID {p.Id,-6} {name,-28} {memMb,5} MB {(elevated ? "[elevated]" : "")} {(string.IsNullOrEmpty(title) ? "" : $"\"{title}\"")}");
            }
            catch { }
            finally { p.Dispose(); }
        }

        return (string.Join("\n", lines), new { count = list.Count, processes = list });
    }

    public static (string Text, object Data) KillProcess(int? pid, string? name, bool tree = true)
    {
        if (pid.HasValue)
        {
            try
            {
                using var p = Process.GetProcessById(pid.Value);
                string pName = p.ProcessName;
                p.Kill(entireProcessTree: tree);
                return ($"Terminated process {pName} (PID={pid.Value}, tree={tree}).", new { pid = pid.Value, name = pName, tree });
            }
            catch (Exception ex)
            {
                throw new HelperErrorException("not_found", $"PID={pid.Value} sonlandırılamadı: {ex.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            string clean = name.Trim();
            if (clean.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) clean = clean[..^4];
            var procs = Process.GetProcessesByName(clean);
            if (procs.Length == 0)
            {
                throw new HelperErrorException("not_found", $"Çalışan süreç bulunamadı: {clean}");
            }
            int killed = 0;
            foreach (var p in procs)
            {
                try
                {
                    p.Kill(entireProcessTree: tree);
                    killed++;
                }
                catch { }
                finally { p.Dispose(); }
            }
            return ($"Terminated {killed} process(es) named {clean}.", new { name = clean, killed, tree });
        }

        throw new HelperErrorException("bad_request", "kill_process için pid veya name belirtin.");
    }

    public static (string Text, object Data) GetSystemInfo(CaptureEngine capture)
    {
        var mem = new MEMORYSTATUSEX();
        GlobalMemoryStatusEx(mem);
        ulong totalRamMb = mem.ullTotalPhys / (1024 * 1024);
        ulong availRamMb = mem.ullAvailPhys / (1024 * 1024);

        var drives = DriveInfo.GetDrives()
            .Where(d => d.IsReady)
            .Select(d => new
            {
                name = d.Name,
                format = d.DriveFormat,
                totalGb = Math.Round(d.TotalSize / 1e9, 1),
                freeGb = Math.Round(d.AvailableFreeSpace / 1e9, 1)
            })
            .ToList();

        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(n => new { name = n.Name, description = n.Description, type = n.NetworkInterfaceType.ToString() })
            .ToList();

        var monitors = capture.ListMonitors();
        var powerStatus = System.Windows.Forms.SystemInformation.PowerStatus;

        var info = new
        {
            osVersion = Environment.OSVersion.ToString(),
            machineName = Environment.MachineName,
            userName = Environment.UserName,
            processorCount = Environment.ProcessorCount,
            is64BitOs = Environment.Is64BitOperatingSystem,
            uptimeMinutes = Math.Round(Environment.TickCount64 / 60000.0, 1),
            memory = new { totalMb = totalRamMb, availableMb = availRamMb, loadPercent = mem.dwMemoryLoad },
            battery = new
            {
                status = powerStatus.BatteryChargeStatus.ToString(),
                powerLine = powerStatus.PowerLineStatus.ToString(),
                lifePercent = Math.Round(powerStatus.BatteryLifePercent * 100, 0)
            },
            drives,
            networkAdapters = adapters,
            monitors = monitors.Select(m => new { m.Index, m.DeviceName, m.Primary, m.Scale, m.Bounds })
        };

        string summary =
            $"OS: {info.osVersion} ({info.machineName})\n" +
            $"CPU Cores: {info.processorCount} | RAM: {availRamMb} MB free / {totalRamMb} MB total ({mem.dwMemoryLoad}% used)\n" +
            $"Drives: {string.Join(", ", drives.Select(d => $"{d.name} {d.freeGb}/{d.totalGb} GB free"))}\n" +
            $"Monitors: {monitors.Count} ({string.Join(", ", monitors.Select(m => $"#{m.Index} scale={m.Scale * 100}%"))})";

        return (summary, info);
    }

    public static (string Text, object Data) OpenPath(string target)
    {
        string expanded = Environment.ExpandEnvironmentVariables(target.Trim());
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = expanded,
                UseShellExecute = true
            });
            string note = expanded.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                          expanded.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? " (Not: Web sayfalarıyla etkileşim için Prowser MCP new_window aracını tercih edin.)"
                : "";
            return ($"Opened \"{expanded}\" with default application.{note}", new { target = expanded });
        }
        catch (Exception ex)
        {
            throw new HelperErrorException("failed", $"Hedef açılamadı (\"{expanded}\"): {ex.Message}");
        }
    }
}
