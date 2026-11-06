using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using Prodesk.Helper.Native;
using Prodesk.Helper.Uia;

namespace Prodesk.Helper.Capture;

public class MonitorInfoDto
{
    public int Index { get; set; }
    public string DeviceName { get; set; } = "";
    public bool Primary { get; set; }
    public uint DpiX { get; set; } = 96;
    public uint DpiY { get; set; } = 96;
    public double Scale { get; set; } = 1.0;
    public object Bounds { get; set; } = new();
    public object WorkArea { get; set; } = new();
    public Win32.RECT RawBounds { get; set; }
}

public class ScreenshotResultDto
{
    public string Base64Data { get; set; } = "";
    public string MimeType { get; set; } = "image/jpeg";
    public int OriginX { get; set; }
    public int OriginY { get; set; }
    public double Scale { get; set; } = 1.0;
    public int Width { get; set; }
    public int Height { get; set; }
    public int PhysicalWidth { get; set; }
    public int PhysicalHeight { get; set; }
    public string Summary { get; set; } = "";
}

public class CaptureEngine
{
    private readonly UiaEngine _uia;

    public CaptureEngine(UiaEngine uia)
    {
        _uia = uia;
    }

    public List<MonitorInfoDto> ListMonitors()
    {
        var monitors = new List<MonitorInfoDto>();
        int idx = 0;

        Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdcMonitor, ref Win32.RECT lprcMonitor, IntPtr dwData) =>
        {
            var mi = new Win32.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFOEX>() };
            if (Win32.GetMonitorInfo(hMonitor, ref mi))
            {
                uint dpiX = 96, dpiY = 96;
                try { Win32.GetDpiForMonitor(hMonitor, 0, out dpiX, out dpiY); } catch { }
                double scale = Math.Round(dpiX / 96.0, 2);

                monitors.Add(new MonitorInfoDto
                {
                    Index = idx++,
                    DeviceName = mi.szDevice,
                    Primary = (mi.dwFlags & 1) != 0,
                    DpiX = dpiX,
                    DpiY = dpiY,
                    Scale = scale,
                    RawBounds = mi.rcMonitor,
                    Bounds = new
                    {
                        x = mi.rcMonitor.Left,
                        y = mi.rcMonitor.Top,
                        width = mi.rcMonitor.Width,
                        height = mi.rcMonitor.Height
                    },
                    WorkArea = new
                    {
                        x = mi.rcWork.Left,
                        y = mi.rcWork.Top,
                        width = mi.rcWork.Width,
                        height = mi.rcWork.Height
                    }
                });
            }
            return true;
        }, IntPtr.Zero);

        return monitors;
    }

    public Bitmap CaptureRawBitmap(
        string target,
        string? windowQuery,
        string? uid,
        int? monitorIndex,
        Win32.RECT? customRegion,
        bool restoreIfMinimized,
        out int originX,
        out int originY,
        out IntPtr capturedWindowHwnd)
    {
        capturedWindowHwnd = IntPtr.Zero;

        if (!string.IsNullOrWhiteSpace(uid) || target == "element")
        {
            var el = _uia.ResolveElementByUid(uid!);
            capturedWindowHwnd = el.WindowHwnd;
            var r = el.Bounds;
            if (r.Width <= 0 || r.Height <= 0)
                throw new HelperErrorException("failed", $"Öğe \"{uid}\" geçerli bir ekran alanına sahip değil.");
            originX = r.Left;
            originY = r.Top;
            return CaptureScreenRegion(r.Left, r.Top, r.Width, r.Height);
        }

        if (!string.IsNullOrWhiteSpace(windowQuery) || target == "window")
        {
            var win = _uia.ResolveWindow(windowQuery)
                ?? throw new HelperErrorException("not_found", $"Pencere bulunamadı: \"{windowQuery}\"");
            capturedWindowHwnd = win.Hwnd;

            if (Win32.IsIconic(win.Hwnd))
            {
                if (restoreIfMinimized)
                {
                    Win32.ShowWindow(win.Hwnd, Win32.SW_SHOWNOACTIVATE);
                    Thread.Sleep(150);
                }
                else
                {
                    throw new HelperErrorException("failed", $"Pencere \"{win.Title}\" simge durumunda. Yakalamak için restore_if_minimized:true kullanın.");
                }
            }

            Win32.GetWindowRect(win.Hwnd, out var rect);
            originX = rect.Left;
            originY = rect.Top;
            int w = Math.Max(1, rect.Width);
            int h = Math.Max(1, rect.Height);

            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                bool printed = false;
                try
                {
                    // PrintWindow with PW_RENDERFULLCONTENT captures occluded/background windows cleanly (§3.5)
                    printed = Win32.PrintWindow(win.Hwnd, hdc, Win32.PW_RENDERFULLCONTENT);
                }
                finally
                {
                    g.ReleaseHdc(hdc);
                }

                if (!printed)
                {
                    g.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
                }
            }
            return bmp;
        }

        if (target == "region" && customRegion.HasValue)
        {
            var r = customRegion.Value;
            originX = r.Left;
            originY = r.Top;
            return CaptureScreenRegion(r.Left, r.Top, r.Width, r.Height);
        }

        if (target == "monitor" || monitorIndex.HasValue)
        {
            var monitors = ListMonitors();
            int mIdx = monitorIndex ?? 0;
            var mon = monitors.FirstOrDefault(m => m.Index == mIdx) ?? monitors.FirstOrDefault()
                ?? throw new HelperErrorException("not_found", $"Monitör bulunamadı: {mIdx}");
            originX = mon.RawBounds.Left;
            originY = mon.RawBounds.Top;
            return CaptureScreenRegion(mon.RawBounds.Left, mon.RawBounds.Top, mon.RawBounds.Width, mon.RawBounds.Height);
        }

        // Default: capture primary monitor or full virtual screen
        var allMonitors = ListMonitors();
        var primary = allMonitors.FirstOrDefault(m => m.Primary) ?? allMonitors.FirstOrDefault();
        if (primary != null)
        {
            originX = primary.RawBounds.Left;
            originY = primary.RawBounds.Top;
            return CaptureScreenRegion(primary.RawBounds.Left, primary.RawBounds.Top, primary.RawBounds.Width, primary.RawBounds.Height);
        }

        originX = Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN);
        originY = Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN);
        int vw = Math.Max(1, Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN));
        int vh = Math.Max(1, Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN));
        return CaptureScreenRegion(originX, originY, vw, vh);
    }

    public ScreenshotResultDto TakeScreenshot(
        string target = "screen",
        string? windowQuery = null,
        string? uid = null,
        int? monitorIndex = null,
        Win32.RECT? region = null,
        bool annotate = false,
        string format = "jpeg",
        int quality = 80,
        int maxWidth = 1280,
        bool includeCursor = false,
        bool restoreIfMinimized = true)
    {
        using var rawBmp = CaptureRawBitmap(
            target, windowQuery, uid, monitorIndex, region, restoreIfMinimized,
            out int originX, out int originY, out IntPtr capturedHwnd);

        int physW = rawBmp.Width;
        int physH = rawBmp.Height;

        using (var g = Graphics.FromImage(rawBmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (includeCursor && Win32.GetCursorPos(out var curPt))
            {
                int cx = curPt.X - originX;
                int cy = curPt.Y - originY;
                if (cx >= 0 && cy >= 0 && cx < physW && cy < physH)
                {
                    using var pen = new Pen(Color.FromArgb(230, 239, 68, 68), 3f);
                    g.DrawEllipse(pen, cx - 10, cy - 10, 20, 20);
                }
            }

            // Set-of-Mark annotations (§3.3)
            if (annotate)
            {
                IntPtr targetHwnd = capturedHwnd != IntPtr.Zero ? capturedHwnd : Win32.GetForegroundWindow();
                if (targetHwnd == IntPtr.Zero)
                {
                    targetHwnd = _uia.EnumerateTopWindows().FirstOrDefault()?.Hwnd ?? IntPtr.Zero;
                }
                var elems = _uia.GetRecentWindowInteractiveElements(targetHwnd);
                if (elems.Count == 0 && targetHwnd != IntPtr.Zero)
                {
                    try { _uia.BuildSnapshot(windowQuery, interactiveOnly: true, maxDepth: 6); } catch { }
                    elems = _uia.GetRecentWindowInteractiveElements(targetHwnd);
                }

                using var boxPen = new Pen(Color.FromArgb(220, 14, 165, 233), 2f);
                using var badgeBrush = new SolidBrush(Color.FromArgb(230, 2, 132, 199));
                using var textBrush = new SolidBrush(Color.White);
                using var font = new Font("Segoe UI", 9f, FontStyle.Bold);

                foreach (var el in elems)
                {
                    int rx = el.Bounds.Left - originX;
                    int ry = el.Bounds.Top - originY;
                    int rw = el.Bounds.Width;
                    int rh = el.Bounds.Height;
                    if (rx < 0 || ry < 0 || rx + rw > physW || ry + rh > physH) continue;

                    g.DrawRectangle(boxPen, rx, ry, rw, rh);
                    string tag = el.Uid;
                    var sz = g.MeasureString(tag, font);
                    float by = Math.Max(0, ry - sz.Height);
                    g.FillRectangle(badgeBrush, rx, by, sz.Width + 6, sz.Height + 2);
                    g.DrawString(tag, font, textBrush, rx + 3, by + 1);
                }
            }
        }

        double scale = 1.0;
        int outW = physW;
        int outH = physH;
        if (maxWidth > 0 && physW > maxWidth)
        {
            scale = (double)maxWidth / physW;
            outW = Math.Max(1, (int)Math.Round(physW * scale));
            outH = Math.Max(1, (int)Math.Round(physH * scale));
        }

        Bitmap finalBmp = rawBmp;
        bool disposeFinal = false;
        if (outW != physW || outH != physH)
        {
            finalBmp = new Bitmap(outW, outH, PixelFormat.Format24bppRgb);
            disposeFinal = true;
            using var g2 = Graphics.FromImage(finalBmp);
            g2.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g2.DrawImage(rawBmp, 0, 0, outW, outH);
        }

        try
        {
            using var ms = new MemoryStream();
            string mime;
            if (string.Equals(format, "png", StringComparison.OrdinalIgnoreCase))
            {
                finalBmp.Save(ms, ImageFormat.Png);
                mime = "image/png";
            }
            else
            {
                var jpgEncoder = ImageCodecInfo.GetImageDecoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                using var encParams = new EncoderParameters(1);
                encParams.Param[0] = new EncoderParameter(Encoder.Quality, Math.Clamp(quality, 10, 100));
                finalBmp.Save(ms, jpgEncoder, encParams);
                mime = "image/jpeg";
            }

            string b64 = Convert.ToBase64String(ms.ToArray());
            return new ScreenshotResultDto
            {
                Base64Data = b64,
                MimeType = mime,
                OriginX = originX,
                OriginY = originY,
                Scale = scale,
                Width = outW,
                Height = outH,
                PhysicalWidth = physW,
                PhysicalHeight = physH,
                Summary = $"Captured {outW}x{outH} ({mime}, physical {physW}x{physH} at origin {originX},{originY}, scale={scale:F4})"
            };
        }
        finally
        {
            if (disposeFinal) finalBmp.Dispose();
        }
    }

    private Bitmap CaptureScreenRegion(int x, int y, int width, int height)
    {
        int w = Math.Max(1, width);
        int h = Math.Max(1, height);
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(15, 23, 42)); // Clean dark slate background

        bool copied = false;
        try
        {
            g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
            copied = true;
        }
        catch { }

        if (!copied)
        {
            // Headless / non-interactive session fallback:
            // Render visible top windows in z-order (bottom to top) onto canvas via PrintWindow (§3.5)
            try
            {
                var windows = _uia.EnumerateTopWindows(includeMinimized: false);
                windows.Reverse();
                foreach (var win in windows)
                {
                    var wr = win.Bounds;
                    if (wr.Width <= 0 || wr.Height <= 0) continue;
                    int dx = wr.Left - x;
                    int dy = wr.Top - y;
                    if (dx + wr.Width < 0 || dy + wr.Height < 0 || dx >= w || dy >= h) continue;

                    try
                    {
                        using var winBmp = new Bitmap(wr.Width, wr.Height, PixelFormat.Format32bppArgb);
                        using var winG = Graphics.FromImage(winBmp);
                        IntPtr winHdc = winG.GetHdc();
                        try
                        {
                            Win32.PrintWindow(win.Hwnd, winHdc, Win32.PW_RENDERFULLCONTENT);
                        }
                        finally
                        {
                            winG.ReleaseHdc(winHdc);
                        }
                        g.DrawImage(winBmp, dx, dy, wr.Width, wr.Height);
                    }
                    catch { }
                }
            }
            catch { }
        }

        return bmp;
    }
}
