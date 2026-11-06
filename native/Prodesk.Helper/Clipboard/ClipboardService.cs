using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Prodesk.Helper.Native;
using Prodesk.Helper.Uia;

namespace Prodesk.Helper.Clipboard;

public class ClipboardHistoryEntry
{
    public string Timestamp { get; set; } = DateTime.UtcNow.ToString("O");
    public string Format { get; set; } = "text";
    public string Preview { get; set; } = "";
    public string Source { get; set; } = "ring_buffer";
}

public class ClipboardService
{
    private readonly object _ringLock = new();
    private readonly LinkedList<ClipboardHistoryEntry> _ringBuffer = new();
    private const int MaxRingItems = 100;
    private string _lastRecordedText = "";

    private readonly uint _cfExcludeMonitor;
    private readonly uint _cfCanIncludeHistory;

    public ClipboardService()
    {
        _cfExcludeMonitor = Win32.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
        _cfCanIncludeHistory = Win32.RegisterClipboardFormat("CanIncludeInClipboardHistory");
    }

    public static T RunSta<T>(Func<T> action)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                tcs.TrySetResult(action());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task.GetAwaiter().GetResult();
    }

    public bool IsCurrentClipboardExcludedByPasswordManager()
    {
        return Win32.IsClipboardFormatAvailable(_cfExcludeMonitor);
    }

    public void PollAndRecordClipboardIfChanged()
    {
        try
        {
            if (IsCurrentClipboardExcludedByPasswordManager()) return;
            string? text = RunSta(() => System.Windows.Forms.Clipboard.ContainsText() ? System.Windows.Forms.Clipboard.GetText() : null);
            if (!string.IsNullOrEmpty(text) && text != _lastRecordedText)
            {
                _lastRecordedText = text;
                RecordToRingBuffer("text", text);
            }
        }
        catch { }
    }

    public void RecordToRingBuffer(string format, string preview)
    {
        if (string.IsNullOrEmpty(preview)) return;
        lock (_ringLock)
        {
            if (_ringBuffer.First != null && _ringBuffer.First.Value.Preview == preview) return;
            _ringBuffer.AddFirst(new ClipboardHistoryEntry
            {
                Timestamp = DateTime.UtcNow.ToString("O"),
                Format = format,
                Preview = preview.Length > 500 ? preview[..500] + "..." : preview,
                Source = "ring_buffer"
            });
            while (_ringBuffer.Count > MaxRingItems)
            {
                _ringBuffer.RemoveLast();
            }
        }
    }

    public (string Text, object Data, string? ImageBase64) ReadClipboard(string format = "text")
    {
        return RunSta<(string Text, object Data, string? ImageBase64)>(() =>
        {
            string fmt = format.ToLowerInvariant();
            if (fmt == "image")
            {
                if (!System.Windows.Forms.Clipboard.ContainsImage())
                    return ("(clipboard does not contain an image)", (object)new { format = "image", hasData = false }, (string?)null);
                using var img = System.Windows.Forms.Clipboard.GetImage()!;
                using var ms = new MemoryStream();
                img.Save(ms, ImageFormat.Png);
                string b64 = Convert.ToBase64String(ms.ToArray());
                return ($"Read clipboard image ({img.Width}x{img.Height}).", (object)new { format = "image", width = img.Width, height = img.Height }, (string?)b64);
            }
            if (fmt == "files")
            {
                if (!System.Windows.Forms.Clipboard.ContainsFileDropList())
                    return ("(clipboard does not contain files)", (object)new { format = "files", files = Array.Empty<string>() }, (string?)null);
                var list = System.Windows.Forms.Clipboard.GetFileDropList().Cast<string>().ToArray();
                return (string.Join("\n", list), (object)new { format = "files", files = list }, (string?)null);
            }
            if (fmt == "html")
            {
                string html = System.Windows.Forms.Clipboard.GetText(System.Windows.Forms.TextDataFormat.Html) ?? "";
                return (html, (object)new { format = "html", length = html.Length }, (string?)null);
            }
            if (fmt == "rtf")
            {
                string rtf = System.Windows.Forms.Clipboard.GetText(System.Windows.Forms.TextDataFormat.Rtf) ?? "";
                return (rtf, (object)new { format = "rtf", length = rtf.Length }, (string?)null);
            }

            string txt = System.Windows.Forms.Clipboard.ContainsText() ? System.Windows.Forms.Clipboard.GetText() : "";
            if (!string.IsNullOrEmpty(txt) && !IsCurrentClipboardExcludedByPasswordManager())
            {
                RecordToRingBuffer("text", txt);
            }
            return (txt, (object)new { format = "text", length = txt.Length }, (string?)null);
        });
    }

    public void WriteText(string text)
    {
        RunSta(() =>
        {
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    System.Windows.Forms.Clipboard.SetText(text ?? "", System.Windows.Forms.TextDataFormat.UnicodeText);
                    RecordToRingBuffer("text", text ?? "");
                    return true;
                }
                catch
                {
                    Thread.Sleep(30);
                }
            }
            throw new HelperErrorException("failed", "Pano başka bir süreç tarafından kilitli olduğu için yazılamadı.");
        });
    }

    public (string Text, object Data) WriteClipboard(string format, string? text, string[]? files)
    {
        return RunSta(() =>
        {
            string fmt = format.ToLowerInvariant();
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (fmt == "files" && files != null)
                    {
                        var sc = new StringCollection();
                        sc.AddRange(files.Select(Environment.ExpandEnvironmentVariables).ToArray());
                        System.Windows.Forms.Clipboard.SetFileDropList(sc);
                        return ($"Wrote {files.Length} file(s) to clipboard.", (object)new { format = "files", count = files.Length });
                    }
                    if (fmt == "image" && !string.IsNullOrEmpty(text))
                    {
                        byte[] bytes = Convert.FromBase64String(text);
                        using var ms = new MemoryStream(bytes);
                        using var bmp = new Bitmap(ms);
                        System.Windows.Forms.Clipboard.SetImage(bmp);
                        return ($"Wrote {bmp.Width}x{bmp.Height} image to clipboard.", new { format = "image", width = bmp.Width, height = bmp.Height });
                    }
                    if (fmt == "html")
                    {
                        System.Windows.Forms.Clipboard.SetText(text ?? "", System.Windows.Forms.TextDataFormat.Html);
                        return ("Wrote HTML to clipboard.", new { format = "html" });
                    }
                    if (fmt == "rtf")
                    {
                        System.Windows.Forms.Clipboard.SetText(text ?? "", System.Windows.Forms.TextDataFormat.Rtf);
                        return ("Wrote RTF to clipboard.", new { format = "rtf" });
                    }

                    System.Windows.Forms.Clipboard.SetText(text ?? "", System.Windows.Forms.TextDataFormat.UnicodeText);
                    RecordToRingBuffer("text", text ?? "");
                    return ($"Wrote {(text ?? "").Length} char(s) to clipboard.", new { format = "text", length = (text ?? "").Length });
                }
                catch
                {
                    Thread.Sleep(30);
                }
            }
            throw new HelperErrorException("failed", "Panoya yazılamadı.");
        });
    }

    public void ClearClipboard()
    {
        RunSta(() =>
        {
            System.Windows.Forms.Clipboard.Clear();
            return true;
        });
    }

    public async Task<(string Text, object Data)> GetClipboardHistoryAsync(int maxItems = 25)
    {
        PollAndRecordClipboardIfChanged();
        var items = new List<ClipboardHistoryEntry>();
        string source = "ring_buffer";

        // 1. Try Windows.ApplicationModel.DataTransfer.Clipboard.GetHistoryItemsAsync (§5.7)
        try
        {
            if (global::Windows.ApplicationModel.DataTransfer.Clipboard.IsHistoryEnabled())
            {
                var winHistory = await global::Windows.ApplicationModel.DataTransfer.Clipboard.GetHistoryItemsAsync();
                if (winHistory.Status == global::Windows.ApplicationModel.DataTransfer.ClipboardHistoryItemsResultStatus.Success)
                {
                    source = "windows_history+ring_buffer";
                    foreach (var hItem in winHistory.Items.Take(maxItems))
                    {
                        if (hItem.Content.Contains(global::Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                        {
                            string t = await hItem.Content.GetTextAsync();
                            items.Add(new ClipboardHistoryEntry
                            {
                                Timestamp = hItem.Timestamp.UtcDateTime.ToString("O"),
                                Format = "text",
                                Preview = t.Length > 500 ? t[..500] + "..." : t,
                                Source = "windows_history"
                            });
                        }
                    }
                }
            }
        }
        catch { }

        // 2. Merge Helper in-memory ring buffer
        lock (_ringLock)
        {
            foreach (var r in _ringBuffer)
            {
                if (items.Count >= maxItems) break;
                if (!items.Any(i => i.Preview == r.Preview))
                {
                    items.Add(r);
                }
            }
        }

        string summary = items.Count > 0
            ? string.Join("\n", items.Select((it, idx) => $"{idx + 1}. [{it.Source}] ({it.Timestamp}): {it.Preview}"))
            : "(no clipboard history items)";

        return (summary, new { source, count = items.Count, items });
    }

    public Dictionary<string, object> BackupClipboard()
    {
        return RunSta(() =>
        {
            var dict = new Dictionary<string, object>();
            try
            {
                var dataObj = System.Windows.Forms.Clipboard.GetDataObject();
                if (dataObj == null) return dict;
                foreach (var fmt in dataObj.GetFormats(false))
                {
                    try
                    {
                        var val = dataObj.GetData(fmt, false);
                        if (val != null) dict[fmt] = val;
                    }
                    catch { }
                }
            }
            catch { }
            return dict;
        });
    }

    public void RestoreClipboard(Dictionary<string, object> backup)
    {
        RunSta(() =>
        {
            try
            {
                if (backup.Count == 0)
                {
                    System.Windows.Forms.Clipboard.Clear();
                    return true;
                }
                var dataObj = new System.Windows.Forms.DataObject();
                foreach (var kv in backup)
                {
                    dataObj.SetData(kv.Key, false, kv.Value);
                }
                System.Windows.Forms.Clipboard.SetDataObject(dataObj, true, 5, 30);
            }
            catch { }
            return true;
        });
    }
}
