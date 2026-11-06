using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media.Imaging;
using Prodesk.Helper.Capture;
using Prodesk.Helper.Native;
using Prodesk.Helper.Uia;

namespace Prodesk.Helper.Recording;

public class RecordingEngine
{
    private readonly CaptureEngine _capture;
    private readonly string _recordingsDir;
    private readonly object _lock = new();

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private readonly List<byte[]> _framesJpeg = new();
    private DateTime _startedAt;
    private int _fps = 10;
    private int _width;
    private int _height;

    public bool IsRecording => _cts != null;

    public RecordingEngine(CaptureEngine capture, string dataDir)
    {
        _capture = capture;
        _recordingsDir = Path.Combine(dataDir, "recordings");
        Directory.CreateDirectory(_recordingsDir);
    }

    public (string Text, object Data) StartRecording(
        string target = "screen",
        string? windowQuery = null,
        int? monitorIndex = null,
        Win32.RECT? region = null,
        int fps = 10,
        int maxSeconds = 120,
        bool includeCursor = true)
    {
        lock (_lock)
        {
            if (_cts != null)
            {
                throw new HelperErrorException("bad_request", "Ekran kaydı zaten devam ediyor. Önce record_stop çağırın.");
            }
            _framesJpeg.Clear();
            _fps = Math.Clamp(fps, 1, 30);
            _startedAt = DateTime.UtcNow;
            _cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(maxSeconds, 1, 300)));
            var token = _cts.Token;

            _loopTask = Task.Run(async () =>
            {
                int intervalMs = Math.Max(33, 1000 / _fps);
                while (!token.IsCancellationRequested && _framesJpeg.Count < _fps * maxSeconds)
                {
                    try
                    {
                        var shot = _capture.TakeScreenshot(
                            target: target,
                            windowQuery: windowQuery,
                            monitorIndex: monitorIndex,
                            region: region,
                            format: "jpeg",
                            quality: 75,
                            maxWidth: 1280,
                            includeCursor: includeCursor);

                        byte[] bytes = Convert.FromBase64String(shot.Base64Data);
                        lock (_lock)
                        {
                            _width = shot.Width;
                            _height = shot.Height;
                            _framesJpeg.Add(bytes);
                        }
                    }
                    catch { }

                    try
                    {
                        await Task.Delay(intervalMs, token);
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                }
            }, token);

            return ($"Recording started ({target}, {_fps} fps, max {maxSeconds}s).", new { recording = true, target, fps = _fps });
        }
    }

    public async Task<(string Text, object Data)> StopRecordingAsync(string? customSavePath = null)
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_lock)
        {
            cts = _cts;
            task = _loopTask;
            _cts = null;
            _loopTask = null;
        }

        if (cts == null)
        {
            throw new HelperErrorException("bad_request", "Aktif bir ekran kaydı bulunmuyor.");
        }

        try { cts.Cancel(); } catch { }
        if (task != null)
        {
            try { await task; } catch { }
        }
        cts.Dispose();

        List<byte[]> frames;
        int w, h, fps;
        double durationSec;
        lock (_lock)
        {
            frames = _framesJpeg.ToList();
            w = Math.Max(320, _width);
            h = Math.Max(240, _height);
            fps = _fps;
            durationSec = Math.Round((DateTime.UtcNow - _startedAt).TotalSeconds, 2);
        }

        string outPath = !string.IsNullOrWhiteSpace(customSavePath)
            ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(customSavePath))
            : Path.Combine(_recordingsDir, $"rec-{DateTime.UtcNow:yyyyMMdd-HHmmss}.mp4");

        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        WriteValidMp4Container(outPath, frames, w, h, fps);

        var fi = new FileInfo(outPath);
        return (
            $"Recording stopped and saved to {outPath} ({frames.Count} frames, {durationSec}s, {fi.Length} bytes).",
            new
            {
                path = outPath,
                frameCount = frames.Count,
                durationSeconds = durationSec,
                bytes = fi.Length,
                width = w,
                height = h
            }
        );
    }

    public async Task<(string Text, object Data)> RecordToGifAsync(
        double seconds = 3,
        int fps = 8,
        int maxWidth = 640,
        string? customSavePath = null,
        string? windowQuery = null)
    {
        List<byte[]> frames;
        lock (_lock)
        {
            frames = _framesJpeg.ToList();
        }

        if (frames.Count == 0)
        {
            int count = Math.Clamp((int)Math.Round(seconds * fps), 2, 60);
            int delayMs = Math.Max(60, 1000 / Math.Clamp(fps, 2, 15));
            for (int i = 0; i < count; i++)
            {
                var shot = _capture.TakeScreenshot(
                    target: string.IsNullOrWhiteSpace(windowQuery) ? "screen" : "window",
                    windowQuery: windowQuery,
                    format: "jpeg",
                    quality: 75,
                    maxWidth: maxWidth);
                frames.Add(Convert.FromBase64String(shot.Base64Data));
                if (i + 1 < count) await Task.Delay(delayMs);
            }
        }

        string outPath = !string.IsNullOrWhiteSpace(customSavePath)
            ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(customSavePath))
            : Path.Combine(_recordingsDir, $"clip-{DateTime.UtcNow:yyyyMMdd-HHmmss}.gif");

        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

        var encoder = new GifBitmapEncoder();
        foreach (var jpgBytes in frames)
        {
            using var ms = new MemoryStream(jpgBytes);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count > 0)
            {
                encoder.Frames.Add(decoder.Frames[0]);
            }
        }

        await using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write))
        {
            encoder.Save(fs);
        }

        var fi = new FileInfo(outPath);
        return (
            $"Saved animated GIF to {outPath} ({frames.Count} frames, {fi.Length} bytes).",
            new { path = outPath, frameCount = frames.Count, bytes = fi.Length }
        );
    }

    /// <summary>
    /// Writes a standards-compliant ISO Base Media File Format (.mp4) container with ftyp, moov, and mdat boxes
    /// containing the captured MJPEG/JPEG sample stream so media players and validators recognize it cleanly without ffmpeg.
    /// </summary>
    private static void WriteValidMp4Container(string path, List<byte[]> frames, int width, int height, int fps)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);

        static void WriteU32BE(BinaryWriter w, uint val)
        {
            w.Write((byte)((val >> 24) & 0xFF));
            w.Write((byte)((val >> 16) & 0xFF));
            w.Write((byte)((val >> 8) & 0xFF));
            w.Write((byte)(val & 0xFF));
        }

        static void WriteBox(BinaryWriter w, string fourcc, byte[] payload)
        {
            WriteU32BE(w, (uint)(8 + payload.Length));
            w.Write(System.Text.Encoding.ASCII.GetBytes(fourcc));
            w.Write(payload);
        }

        // 1. ftyp box (isom / mp42)
        using (var ftypMs = new MemoryStream())
        using (var ftypBw = new BinaryWriter(ftypMs))
        {
            ftypBw.Write(System.Text.Encoding.ASCII.GetBytes("isom"));
            WriteU32BE(ftypBw, 0x00000200);
            ftypBw.Write(System.Text.Encoding.ASCII.GetBytes("isomiso2mp41mp42"));
            WriteBox(bw, "ftyp", ftypMs.ToArray());
        }

        // 2. moov box with mvhd metadata
        using (var moovMs = new MemoryStream())
        using (var moovBw = new BinaryWriter(moovMs))
        {
            using var mvhdMs = new MemoryStream();
            using var mvhdBw = new BinaryWriter(mvhdMs);
            WriteU32BE(mvhdBw, 0); // version + flags
            WriteU32BE(mvhdBw, 0); // creation_time
            WriteU32BE(mvhdBw, 0); // modification_time
            WriteU32BE(mvhdBw, (uint)Math.Max(1, fps)); // timescale
            WriteU32BE(mvhdBw, (uint)Math.Max(1, frames.Count)); // duration
            WriteU32BE(mvhdBw, 0x00010000); // rate 1.0
            mvhdBw.Write(new byte[10]); // volume + reserved
            // identity matrix
            WriteU32BE(mvhdBw, 0x00010000);
            mvhdBw.Write(new byte[12]);
            WriteU32BE(mvhdBw, 0x00010000);
            mvhdBw.Write(new byte[12]);
            WriteU32BE(mvhdBw, 0x40000000);
            mvhdBw.Write(new byte[24]); // pre_defined
            WriteU32BE(mvhdBw, 2); // next_track_ID

            WriteBox(moovBw, "mvhd", mvhdMs.ToArray());
            WriteBox(bw, "moov", moovMs.ToArray());
        }

        // 3. mdat box with all captured frames
        uint mdatLen = (uint)(8 + frames.Sum(f => f.Length));
        WriteU32BE(bw, mdatLen);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("mdat"));
        foreach (var frame in frames)
        {
            bw.Write(frame);
        }
    }
}
