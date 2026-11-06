using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using Prodesk.Helper.Capture;
using Prodesk.Helper.Clipboard;
using Prodesk.Helper.Input;
using Prodesk.Helper.Ipc;
using Prodesk.Helper.Ocr;
using Prodesk.Helper.Policy;
using Prodesk.Helper.Recording;
using Prodesk.Helper.Ui;
using Prodesk.Helper.Uia;
using Prodesk.Helper.Windows;

namespace Prodesk.Helper;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Prodesk");
        Directory.CreateDirectory(dataDir);
        string logDir = Path.Combine(dataDir, "logs");
        Directory.CreateDirectory(logDir);

        try
        {
            string sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
            // Use Local\ namespace so non-elevated processes never hit Global\ session creation issues
            string mutexName = $@"Local\Prodesk-Helper-{sid}";

            using var mutex = new Mutex(true, mutexName, out bool createdNew);
            if (!createdNew)
            {
                // Another instance of Prodesk Helper is already running for this user (§2.1)
                return 0;
            }

        // Ensure helper.token exists
        string tokenPath = Path.Combine(dataDir, "helper.token");
        string token;
        if (File.Exists(tokenPath) && File.ReadAllText(tokenPath).Trim().Length >= 32)
        {
            token = File.ReadAllText(tokenPath).Trim();
        }
        else
        {
            token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            File.WriteAllText(tokenPath, token);
        }

        string safeUser = new string(Environment.UserName.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? char.ToLowerInvariant(c) : '_').ToArray());
        string pipeName = $"prodesk-{safeUser}";
        File.WriteAllText(Path.Combine(dataDir, "pipe.name"), $@"\\.\pipe\{pipeName}");

        var app = new System.Windows.Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };

        var policy = new PolicyManager(dataDir);
        var uia = new UiaEngine();
        var capture = new CaptureEngine(uia);
        var ocr = new OcrEngineService(capture, uia);
        var clipboard = new ClipboardService();
        var action = new ActionEngine(uia, ocr, clipboard, policy);
        var ui = new HelperUiManager(policy, app.Dispatcher);
        var winMgr = new WindowAndAppManager(uia, ui);
        var recorder = new RecordingEngine(capture, dataDir);

        action.OnOverlayUpdate = bounds => ui.UpdateOverlay(bounds);
        action.OnEmergencyStopTriggered = () => ui.TriggerEmergencyStop(showBalloon: true);

        using var pipeServer = new PipeServer(
            pipeName, token, policy, uia, capture, ocr, clipboard, action, winMgr, recorder, ui);

        ui.ExitRequested += () =>
        {
            app.Dispatcher.InvokeShutdown();
        };

        // Periodic clipboard ring-buffer poller + 10-minute idle auto-shutdown timer (§2.1)
        var idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        idleTimer.Tick += (_, _) =>
        {
            clipboard.PollAndRecordClipboardIfChanged();
            if (!policy.Settings.KeepHelperAlive &&
                pipeServer.ConnectedClientCount == 0 &&
                (DateTime.UtcNow - pipeServer.LastClientActivityUtc) > TimeSpan.FromMinutes(10))
            {
                app.Dispatcher.InvokeShutdown();
            }
        };

            // Start Named Pipe listener immediately before entering WPF message loop
            pipeServer.Start();

            app.Startup += (_, _) =>
            {
                ui.Initialize();
                idleTimer.Start();
            };

            app.Exit += (_, _) =>
            {
                idleTimer.Stop();
                ui.Dispose();
            };

            app.DispatcherUnhandledException += (_, e) =>
            {
                try
                {
                    File.AppendAllText(
                        Path.Combine(logDir, "helper-crash.log"),
                        $"[{DateTime.UtcNow:O}] [DispatcherUnhandledException] {e.Exception}\n");
                }
                catch { }
                e.Handled = true;
            };

            app.Run();
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(logDir, "helper-crash.log"),
                    $"[{DateTime.UtcNow:O}] [FatalMainException] {ex}\n");
            }
            catch { }
            return 1;
        }
    }
}
