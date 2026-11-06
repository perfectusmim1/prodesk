using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using Prodesk.Helper.Capture;
using Prodesk.Helper.Native;
using Prodesk.Helper.Uia;

namespace Prodesk.Helper.Ocr;

public class OcrWordMatch
{
    public string Text { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int CenterX => X + Width / 2;
    public int CenterY => Y + Height / 2;
    public string Source { get; set; } = "ocr";
    public string? Uid { get; set; }
}

public class OcrEngineService
{
    private readonly CaptureEngine _capture;
    private readonly UiaEngine _uia;

    public OcrEngineService(CaptureEngine capture, UiaEngine uia)
    {
        _capture = capture;
        _uia = uia;
    }

    public static List<string> GetInstalledOcrLanguages()
    {
        try
        {
            return OcrEngine.AvailableRecognizerLanguages
                .Select(l => l.LanguageTag)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static OcrEngine CreateOcrEngine(string? preferredLanguage)
    {
        if (!string.IsNullOrWhiteSpace(preferredLanguage))
        {
            try
            {
                var lang = new Language(preferredLanguage);
                if (OcrEngine.IsLanguageSupported(lang))
                {
                    var eng = OcrEngine.TryCreateFromLanguage(lang);
                    if (eng != null) return eng;
                }
            }
            catch { }
        }

        // Prefer Turkish (tr-TR) first on Turkish Windows, then English (en-US), then UserProfileLanguages
        foreach (string tag in new[] { "tr-TR", "tr", "en-US", "en" })
        {
            try
            {
                var lang = new Language(tag);
                if (OcrEngine.IsLanguageSupported(lang))
                {
                    var eng = OcrEngine.TryCreateFromLanguage(lang);
                    if (eng != null) return eng;
                }
            }
            catch { }
        }

        return OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new HelperErrorException("failed", "Windows.Media.Ocr dil paketi bulunamadı (tr-TR veya en-US).");
    }

    public async Task<(string FullText, List<OcrWordMatch> Lines, List<OcrWordMatch> Words)> RunOcrAsync(
        string? windowQuery = null,
        int? monitorIndex = null,
        Win32.RECT? region = null,
        string? language = null)
    {
        string target = !string.IsNullOrWhiteSpace(windowQuery) ? "window"
            : region.HasValue ? "region"
            : monitorIndex.HasValue ? "monitor"
            : "screen";

        using var bmp = _capture.CaptureRawBitmap(
            target, windowQuery, null, monitorIndex, region, restoreIfMinimized: true,
            out int originX, out int originY, out _);

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        ms.Position = 0;

        using var ras = new InMemoryRandomAccessStream();
        await ras.WriteAsync(ms.ToArray().AsBuffer());
        ras.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(ras);
        using var softwareBmp = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        var engine = CreateOcrEngine(language);
        var ocrResult = await engine.RecognizeAsync(softwareBmp);

        var lines = new List<OcrWordMatch>();
        var words = new List<OcrWordMatch>();

        foreach (var line in ocrResult.Lines)
        {
            if (line.Words.Count == 0) continue;
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var w in line.Words)
            {
                var r = w.BoundingRect;
                minX = Math.Min(minX, r.X);
                minY = Math.Min(minY, r.Y);
                maxX = Math.Max(maxX, r.X + r.Width);
                maxY = Math.Max(maxY, r.Y + r.Height);

                words.Add(new OcrWordMatch
                {
                    Text = w.Text,
                    X = originX + (int)Math.Round(r.X),
                    Y = originY + (int)Math.Round(r.Y),
                    Width = Math.Max(1, (int)Math.Round(r.Width)),
                    Height = Math.Max(1, (int)Math.Round(r.Height)),
                    Source = "ocr"
                });
            }

            lines.Add(new OcrWordMatch
            {
                Text = line.Text,
                X = originX + (int)Math.Round(minX),
                Y = originY + (int)Math.Round(minY),
                Width = Math.Max(1, (int)Math.Round(maxX - minX)),
                Height = Math.Max(1, (int)Math.Round(maxY - minY)),
                Source = "ocr"
            });
        }

        return (ocrResult.Text ?? "", lines, words);
    }

    public async Task<List<OcrWordMatch>> FindTextAsync(
        string text,
        bool exact = false,
        string? windowQuery = null,
        string? language = null)
    {
        var matches = new List<OcrWordMatch>();

        // 1. Search UIA elements first (§3.4)
        try
        {
            var uiaHits = await UiaEngine.RunMtaWithTimeoutAsync(
                () => _uia.FindElements(windowQuery, name: text, role: null, automationId: null, className: null, exact: exact, maxResults: 15),
                4000);
            foreach (var el in uiaHits)
            {
                if (el.Bounds.Width > 0 && el.Bounds.Height > 0)
                {
                    matches.Add(new OcrWordMatch
                    {
                        Text = el.Name,
                        X = el.Bounds.Left,
                        Y = el.Bounds.Top,
                        Width = el.Bounds.Width,
                        Height = el.Bounds.Height,
                        Source = "uia",
                        Uid = el.Uid
                    });
                }
            }
        }
        catch { }

        // 2. Fallback or supplement with Windows.Media.Ocr
        var (_, lines, words) = await RunOcrAsync(windowQuery, null, null, language);
        foreach (var line in lines)
        {
            bool hit = exact
                ? string.Equals(line.Text.Trim(), text.Trim(), StringComparison.OrdinalIgnoreCase)
                : line.Text.Contains(text, StringComparison.OrdinalIgnoreCase);
            if (hit) matches.Add(line);
        }
        foreach (var word in words)
        {
            bool hit = exact
                ? string.Equals(word.Text.Trim(), text.Trim(), StringComparison.OrdinalIgnoreCase)
                : word.Text.Contains(text, StringComparison.OrdinalIgnoreCase);
            if (hit && !matches.Any(m => Math.Abs(m.CenterX - word.CenterX) < 8 && Math.Abs(m.CenterY - word.CenterY) < 8))
            {
                matches.Add(word);
            }
        }

        return matches;
    }
}
