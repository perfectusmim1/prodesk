using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Prodesk.Helper.Input;
using Prodesk.Helper.Native;
using Prodesk.Helper.Policy;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Orientation = System.Windows.Controls.Orientation;
using Path = System.IO.Path;

namespace Prodesk.Helper.Ui;

public enum ConfirmResult
{
    AllowOnce,
    AllowSession,
    Deny
}

public class HelperUiManager : IDisposable
{
    private readonly PolicyManager _policy;
    private readonly Dispatcher _dispatcher;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private System.Windows.Forms.ToolStripMenuItem? _statusItem;
    private System.Windows.Forms.ToolStripMenuItem? _autoApproveItem;
    private System.Windows.Forms.ToolStripMenuItem? _pauseItem;
    private ProdeskVisualOverlayWindow? _overlayWindow;
    private SettingsDialogWindow? _settingsWindow;
    private NativeCalculatorWindow? _calcWindow;
    private HwndSource? _hotkeyHwndSource;
    private readonly List<TaskCompletionSource<ConfirmResult>> _pendingConfirms = new();
    private const int HotkeyId = 0x5052;

    public event Action? ExitRequested;

    public HelperUiManager(PolicyManager policy, Dispatcher dispatcher)
    {
        _policy = policy;
        _dispatcher = dispatcher;
        _policy.SettingsChanged += OnSettingsChanged;
    }

    public void Initialize()
    {
        CreateTrayIcon();
        RegisterEmergencyHotkey();
        _overlayWindow = new ProdeskVisualOverlayWindow();
    }

    private void CreateTrayIcon()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();

        _statusItem = new System.Windows.Forms.ToolStripMenuItem("Prodesk: Çalışıyor") { Enabled = false };
        _autoApproveItem = new System.Windows.Forms.ToolStripMenuItem("Soru sormadan otomatik onayla")
        {
            CheckOnClick = true,
            Checked = _policy.Settings.AutoApprove
        };
        _autoApproveItem.Click += (_, _) =>
        {
            _policy.SetAutoApprove(_autoApproveItem.Checked);
        };

        _pauseItem = new System.Windows.Forms.ToolStripMenuItem("Duraklat (Ctrl+Alt+Shift+X)")
        {
            Checked = _policy.Settings.Paused
        };
        _pauseItem.Click += (_, _) =>
        {
            if (!_policy.Settings.Paused)
                TriggerEmergencyStop(showBalloon: false);
            else
                _policy.SetPaused(false);
        };

        var settingsItem = new System.Windows.Forms.ToolStripMenuItem("Ayarlar ve Kayıtlar...");
        settingsItem.Click += (_, _) => ShowSettingsWindow();

        var exitItem = new System.Windows.Forms.ToolStripMenuItem("Çıkış");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        menu.Items.Add(_statusItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(_autoApproveItem);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(settingsItem);
        menu.Items.Add(exitItem);

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = BuildStatusIcon(_policy.Settings.Paused, _policy.Settings.AutoApprove),
            Text = GetTrayTooltip(),
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => ShowSettingsWindow();
    }

    private void RegisterEmergencyHotkey()
    {
        try
        {
            var parameters = new HwndSourceParameters("ProdeskHotkeySink")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0
            };
            _hotkeyHwndSource = new HwndSource(parameters);
            _hotkeyHwndSource.AddHook(WndProc);

            // Ctrl + Alt + Shift + X (VK_X = 0x58)
            uint mods = Win32.MOD_CONTROL | Win32.MOD_ALT | Win32.MOD_SHIFT | Win32.MOD_NOREPEAT;
            Win32.RegisterHotKey(_hotkeyHwndSource.Handle, HotkeyId, mods, 0x58);
        }
        catch { }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            TriggerEmergencyStop(showBalloon: true);
        }
        return IntPtr.Zero;
    }

    public void TriggerEmergencyStop(bool showBalloon = true)
    {
        _policy.SetPaused(true);
        ActionEngine.ReleaseAllModifiers();

        lock (_pendingConfirms)
        {
            foreach (var tcs in _pendingConfirms)
            {
                tcs.TrySetResult(ConfirmResult.Deny);
            }
            _pendingConfirms.Clear();
        }

        UpdateOverlay(null);
        _dispatcher.BeginInvoke(() => _overlayWindow?.DismissImmediate());
        if (showBalloon)
        {
            ShowNotification("Prodesk Acil Durdurma", "Tüm eylemler duraklatıldı ve bekleyen onaylar iptal edildi.", "warning");
        }
    }

    private void OnSettingsChanged()
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (_autoApproveItem != null) _autoApproveItem.Checked = _policy.Settings.AutoApprove;
            if (_pauseItem != null)
            {
                _pauseItem.Checked = _policy.Settings.Paused;
                _pauseItem.Text = _policy.Settings.Paused ? "Devam Et (Ctrl+Alt+Shift+X)" : "Duraklat (Ctrl+Alt+Shift+X)";
            }
            if (_statusItem != null)
            {
                _statusItem.Text = _policy.Settings.Paused
                    ? "Prodesk: Duraklatıldı"
                    : _policy.Settings.AutoApprove
                        ? "Prodesk: Otomatik Onay Açık"
                        : "Prodesk: Çalışıyor";
            }
            if (_trayIcon != null)
            {
                _trayIcon.Icon = BuildStatusIcon(_policy.Settings.Paused, _policy.Settings.AutoApprove);
                _trayIcon.Text = GetTrayTooltip();
            }
        });
    }

    private string GetTrayTooltip()
    {
        if (_policy.Settings.Paused) return "Prodesk — Duraklatıldı";
        if (_policy.Settings.AutoApprove) return "Prodesk — Otomatik Onay Açık";
        return "Prodesk — Arka Plan Otomasyonu Aktif";
    }

    private static Icon BuildStatusIcon(bool paused, bool autoApprove)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var bg = paused
                ? System.Drawing.Color.FromArgb(239, 68, 68)   // Red when paused
                : autoApprove
                    ? System.Drawing.Color.FromArgb(16, 185, 129) // Green when Auto-Approve is ON
                    : System.Drawing.Color.FromArgb(14, 165, 233); // Sky Blue default

            using var brush = new SolidBrush(bg);
            g.FillEllipse(brush, 2, 2, 28, 28);
            using var pen = new System.Drawing.Pen(System.Drawing.Color.White, 2.5f);
            g.DrawRectangle(pen, 8, 9, 16, 12);
            g.DrawLine(pen, 12, 24, 20, 24);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void ShowNotification(string title, string message, string level = "info")
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (_trayIcon == null) return;
            var icon = level == "error" ? System.Windows.Forms.ToolTipIcon.Error
                : level == "warning" ? System.Windows.Forms.ToolTipIcon.Warning
                : System.Windows.Forms.ToolTipIcon.Info;
            _trayIcon.ShowBalloonTip(3500, title, message, icon);
        });
    }

    public void UpdateOverlay(Win32.RECT? bounds)
    {
        _dispatcher.BeginInvoke(() =>
        {
            _overlayWindow?.UpdateTargetBounds(bounds);
        });
    }

    public void ShowVisualAction(VisualActionState state)
    {
        _dispatcher.BeginInvoke(() =>
        {
            _overlayWindow?.ShowVisualAction(state);
        });
    }

    public async Task<ConfirmResult> RequestConfirmationAsync(
        string clientName,
        string tool,
        string risk,
        List<string> reasons,
        string paramsSummary,
        bool allowSession)
    {
        var tcs = new TaskCompletionSource<ConfirmResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingConfirms)
        {
            _pendingConfirms.Add(tcs);
        }

        _ = _dispatcher.BeginInvoke(() =>
        {
            var dlg = new ConfirmDialogWindow(clientName, tool, risk, reasons, paramsSummary, allowSession);
            dlg.Closed += (_, _) =>
            {
                lock (_pendingConfirms)
                {
                    _pendingConfirms.Remove(tcs);
                }
                tcs.TrySetResult(dlg.Result);
            };
            dlg.Show();
        });

        return await tcs.Task;
    }

    public IntPtr ShowOrGetNativeCalculatorWindow()
    {
        IntPtr hwnd = IntPtr.Zero;
        _dispatcher.Invoke(() =>
        {
            if (_calcWindow == null || !_calcWindow.IsLoaded)
            {
                _calcWindow = new NativeCalculatorWindow();
                _calcWindow.Show();
            }
            hwnd = new WindowInteropHelper(_calcWindow).Handle;
        });
        return hwnd;
    }

    public bool CloseNativeCalculatorWindow()
    {
        bool closed = false;
        _dispatcher.Invoke(() =>
        {
            if (_calcWindow != null)
            {
                try { _calcWindow.Close(); } catch { }
                _calcWindow = null;
                closed = true;
            }
        });
        return closed;
    }

    public void ShowSettingsWindow()
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (_settingsWindow != null && _settingsWindow.IsLoaded)
            {
                _settingsWindow.Activate();
                return;
            }
            _settingsWindow = new SettingsDialogWindow(_policy);
            _settingsWindow.Show();
        });
    }

    public void Dispose()
    {
        try
        {
            if (_hotkeyHwndSource != null)
            {
                Win32.UnregisterHotKey(_hotkeyHwndSource.Handle, HotkeyId);
                _hotkeyHwndSource.Dispose();
            }
        }
        catch { }

        _dispatcher.BeginInvoke(() =>
        {
            try { _overlayWindow?.Close(); } catch { }
            _overlayWindow = null;
        });

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
    }
}

/// <summary>
/// High-performance, non-activating, click-through full-screen visual overlay (§4.1).
/// Displays:
/// 1. Feather-soft inward gradient light diffusion (Siri / Apple Intelligence style) along screen edges & corners — zero lines or borders.
/// 2. Top floating glassmorphic Dynamic Island pill with real-time status and emergency stop shortcut (Ctrl+Alt+Shift+X).
/// 3. Apple-style circular frosted-glass virtual cursor (visionOS/iPadOS style) with smooth gliding, motion trail, dual-ring click ripple waves, and drag trails.
/// </summary>
public class ProdeskVisualOverlayWindow : Window
{
    private readonly Grid _rootGrid;
    private readonly Grid _ambientAuraLayer;
    private readonly Border _hudPill;
    private readonly TextBlock _hudTitle;
    private readonly TextBlock _hudDetailText;
    private readonly Border _hudPulseDot;
    private readonly Canvas _canvas;
    private readonly FrameworkElement _appleCursor;
    private readonly ScaleTransform _cursorScale;
    private readonly Border _cursorBadge;
    private readonly TextBlock _cursorBadgeText;
    private readonly System.Windows.Shapes.Ellipse _clickRipple;
    private readonly ScaleTransform _rippleScale;
    private readonly System.Windows.Shapes.Ellipse _clickRippleOuter;
    private readonly ScaleTransform _rippleOuterScale;
    private readonly System.Windows.Shapes.Line _motionTrailLine;
    private readonly System.Windows.Shapes.Line _dragLine;
    private readonly System.Windows.Shapes.Ellipse _dragStartDot;
    private readonly DispatcherTimer _autoHideTimer;
    private double _lastX = -100;
    private double _lastY = -100;
    private bool _isDisposed = false;

    public ProdeskVisualOverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        IsHitTestVisible = false;
        Focusable = false;
        Opacity = 0.0;

        _rootGrid = new Grid();

        // 1. Feather-Soft Inward Ambient Light Diffusion (Zero Borders, Zero Lines, Pure Gradient Falloff)
        _ambientAuraLayer = CreateAmbientLightDiffusionLayer();
        var breathAnim = new DoubleAnimation
        {
            From = 0.65,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(1800),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        _ambientAuraLayer.BeginAnimation(UIElement.OpacityProperty, breathAnim);
        _rootGrid.Children.Add(_ambientAuraLayer);

        // 2. Top Floating Glassmorphic Dynamic Island HUD
        _hudPulseDot = new Border
        {
            Width = 8,
            Height = 8,
            CornerRadius = new CornerRadius(4),
            Background = new LinearGradientBrush(
                Color.FromRgb(56, 189, 248),
                Color.FromRgb(168, 85, 247),
                45.0),
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new DropShadowEffect
            {
                Color = Color.FromRgb(56, 189, 248),
                BlurRadius = 12,
                ShadowDepth = 0,
                Opacity = 0.95
            }
        };
        var dotPulse = new DoubleAnimation
        {
            From = 0.45,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(850),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        _hudPulseDot.BeginAnimation(UIElement.OpacityProperty, dotPulse);

        _hudTitle = new TextBlock
        {
            Text = "Prodesk",
            Foreground = Brushes.White,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 6, 0)
        };

        var sep = new TextBlock
        {
            Text = "·",
            Foreground = new SolidColorBrush(Color.FromArgb(140, 148, 163, 184)),
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };

        _hudDetailText = new TextBlock
        {
            Text = "Hazır",
            Foreground = new SolidColorBrush(Color.FromArgb(230, 226, 232, 240)),
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };

        var stopBadge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(45, 244, 63, 94)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(90, 251, 113, 133)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(8, 2, 9, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock
                    {
                        Text = "⏸ ",
                        FontSize = 9.5,
                        Foreground = new SolidColorBrush(Color.FromRgb(253, 164, 175)),
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    new TextBlock
                    {
                        Text = "Durdur · Ctrl+Alt+Shift+X",
                        FontSize = 10,
                        FontWeight = FontWeights.Medium,
                        Foreground = new SolidColorBrush(Color.FromRgb(254, 205, 211)),
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            }
        };

        var hudContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { _hudPulseDot, _hudTitle, sep, _hudDetailText, stopBadge }
        };

        _hudPill = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(232, 9, 13, 22)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(22),
            Padding = new Thickness(14, 6, 10, 6),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 14, 0, 0),
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 28,
                ShadowDepth = 6,
                Opacity = 0.55
            },
            Child = hudContent
        };
        _rootGrid.Children.Add(_hudPill);

        // 3. Interactive Canvas (Motion Trail, Drag Line, Dual-Ring Click Ripple, Apple Frosted Pointer)
        _canvas = new Canvas { IsHitTestVisible = false };

        var trailBrush = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 1)
        };
        trailBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 56, 189, 248), 0.0));
        trailBrush.GradientStops.Add(new GradientStop(Color.FromArgb(140, 129, 140, 248), 0.6));
        trailBrush.GradientStops.Add(new GradientStop(Color.FromArgb(210, 56, 189, 248), 1.0));

        _motionTrailLine = new System.Windows.Shapes.Line
        {
            Stroke = trailBrush,
            StrokeThickness = 3.0,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Opacity = 0.0,
            Visibility = Visibility.Collapsed
        };
        _canvas.Children.Add(_motionTrailLine);

        _dragLine = new System.Windows.Shapes.Line
        {
            Stroke = new SolidColorBrush(Color.FromArgb(190, 56, 189, 248)),
            StrokeThickness = 2.0,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Visibility = Visibility.Collapsed
        };
        _canvas.Children.Add(_dragLine);

        _dragStartDot = new System.Windows.Shapes.Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = new SolidColorBrush(Color.FromArgb(220, 56, 189, 248)),
            Stroke = Brushes.White,
            StrokeThickness = 1.5,
            Visibility = Visibility.Collapsed
        };
        _canvas.Children.Add(_dragStartDot);

        _rippleOuterScale = new ScaleTransform(1.0, 1.0);
        _clickRippleOuter = new System.Windows.Shapes.Ellipse
        {
            Width = 28,
            Height = 28,
            Stroke = new SolidColorBrush(Color.FromArgb(160, 168, 85, 247)),
            StrokeThickness = 1.5,
            Fill = Brushes.Transparent,
            RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
            RenderTransform = _rippleOuterScale,
            Visibility = Visibility.Collapsed
        };
        _canvas.Children.Add(_clickRippleOuter);

        _rippleScale = new ScaleTransform(1.0, 1.0);
        _clickRipple = new System.Windows.Shapes.Ellipse
        {
            Width = 24,
            Height = 24,
            Stroke = new SolidColorBrush(Color.FromArgb(220, 56, 189, 248)),
            StrokeThickness = 2.0,
            Fill = new RadialGradientBrush(
                Color.FromArgb(65, 56, 189, 248),
                Color.FromArgb(0, 56, 189, 248)),
            RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
            RenderTransform = _rippleScale,
            Visibility = Visibility.Collapsed
        };
        _canvas.Children.Add(_clickRipple);

        _cursorScale = new ScaleTransform(1.0, 1.0);
        var cursorGrid = new Grid
        {
            Width = 22,
            Height = 22,
            RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
            RenderTransform = _cursorScale
        };

        var cursorHalo = new System.Windows.Shapes.Ellipse
        {
            Width = 22,
            Height = 22,
            Fill = new RadialGradientBrush(
                Color.FromArgb(155, 255, 255, 255),
                Color.FromArgb(85, 226, 232, 240)),
            Stroke = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)),
            StrokeThickness = 1.4,
            Effect = new DropShadowEffect
            {
                Color = Color.FromRgb(15, 23, 42),
                BlurRadius = 12,
                ShadowDepth = 2,
                Opacity = 0.42
            }
        };

        var innerDot = new System.Windows.Shapes.Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = new LinearGradientBrush(
                Color.FromRgb(56, 189, 248),
                Color.FromRgb(129, 140, 248),
                45.0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        cursorGrid.Children.Add(cursorHalo);
        cursorGrid.Children.Add(innerDot);

        _appleCursor = cursorGrid;
        Canvas.SetLeft(_appleCursor, -100);
        Canvas.SetTop(_appleCursor, -100);
        _canvas.Children.Add(_appleCursor);

        _cursorBadgeText = new TextBlock
        {
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center
        };
        _cursorBadge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(220, 9, 13, 22)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(55, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(7, 2.5, 7, 2.5),
            Visibility = Visibility.Collapsed,
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 10,
                ShadowDepth = 2,
                Opacity = 0.4
            },
            Child = _cursorBadgeText
        };
        Canvas.SetLeft(_cursorBadge, -100);
        Canvas.SetTop(_cursorBadge, -100);
        _canvas.Children.Add(_cursorBadge);

        _rootGrid.Children.Add(_canvas);
        Content = _rootGrid;

        _autoHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5500) };
        _autoHideTimer.Tick += (_, _) => FadeOut();
    }

    private static Grid CreateAmbientLightDiffusionLayer()
    {
        var layer = new Grid { IsHitTestVisible = false };

        // Soft inward fade helper (0.0 = screen edge, 1.0 = fully transparent inside)
        static LinearGradientBrush CreateEdgeFade(System.Windows.Point start, System.Windows.Point end, Color edgeColor, Color midColor)
        {
            var brush = new LinearGradientBrush { StartPoint = start, EndPoint = end };
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(82, edgeColor.R, edgeColor.G, edgeColor.B), 0.0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(42, midColor.R, midColor.G, midColor.B), 0.32));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(14, midColor.R, midColor.G, midColor.B), 0.68));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, midColor.R, midColor.G, midColor.B), 1.0));
            return brush;
        }

        var cyan = Color.FromRgb(56, 189, 248);
        var indigo = Color.FromRgb(129, 140, 248);
        var violet = Color.FromRgb(192, 132, 252);

        // Top soft light diffusion
        layer.Children.Add(new System.Windows.Shapes.Rectangle
        {
            Height = 95,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            Fill = CreateEdgeFade(new System.Windows.Point(0.5, 0.0), new System.Windows.Point(0.5, 1.0), cyan, indigo)
        });

        // Bottom soft light diffusion
        layer.Children.Add(new System.Windows.Shapes.Rectangle
        {
            Height = 95,
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            Fill = CreateEdgeFade(new System.Windows.Point(0.5, 1.0), new System.Windows.Point(0.5, 0.0), indigo, violet)
        });

        // Left soft light diffusion
        layer.Children.Add(new System.Windows.Shapes.Rectangle
        {
            Width = 95,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
            Fill = CreateEdgeFade(new System.Windows.Point(0.0, 0.5), new System.Windows.Point(1.0, 0.5), cyan, indigo)
        });

        // Right soft light diffusion
        layer.Children.Add(new System.Windows.Shapes.Rectangle
        {
            Width = 95,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Stretch,
            Fill = CreateEdgeFade(new System.Windows.Point(1.0, 0.5), new System.Windows.Point(0.0, 0.5), violet, indigo)
        });

        // 4 Corner radial ambient blooms for seamless organic light wrap
        static System.Windows.Shapes.Rectangle CreateCornerBloom(
            System.Windows.HorizontalAlignment hAlign,
            VerticalAlignment vAlign,
            System.Windows.Point center,
            Color coreColor)
        {
            var radial = new RadialGradientBrush
            {
                Center = center,
                GradientOrigin = center,
                RadiusX = 0.85,
                RadiusY = 0.85
            };
            radial.GradientStops.Add(new GradientStop(Color.FromArgb(75, coreColor.R, coreColor.G, coreColor.B), 0.0));
            radial.GradientStops.Add(new GradientStop(Color.FromArgb(30, coreColor.R, coreColor.G, coreColor.B), 0.45));
            radial.GradientStops.Add(new GradientStop(Color.FromArgb(0, coreColor.R, coreColor.G, coreColor.B), 1.0));

            return new System.Windows.Shapes.Rectangle
            {
                Width = 260,
                Height = 260,
                HorizontalAlignment = hAlign,
                VerticalAlignment = vAlign,
                Fill = radial
            };
        }

        layer.Children.Add(CreateCornerBloom(System.Windows.HorizontalAlignment.Left, VerticalAlignment.Top, new System.Windows.Point(0, 0), cyan));
        layer.Children.Add(CreateCornerBloom(System.Windows.HorizontalAlignment.Right, VerticalAlignment.Top, new System.Windows.Point(1, 0), violet));
        layer.Children.Add(CreateCornerBloom(System.Windows.HorizontalAlignment.Left, VerticalAlignment.Bottom, new System.Windows.Point(0, 1), indigo));
        layer.Children.Add(CreateCornerBloom(System.Windows.HorizontalAlignment.Right, VerticalAlignment.Bottom, new System.Windows.Point(1, 1), violet));

        return layer;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int exStyle = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
        Win32.SetWindowLong(
            hwnd,
            Win32.GWL_EXSTYLE,
            exStyle | Win32.WS_EX_TRANSPARENT | Win32.WS_EX_LAYERED | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE);

        ApplyVirtualScreenBounds();
    }

    private void ApplyVirtualScreenBounds()
    {
        int vx = Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN);
        int vy = Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN);
        int vw = Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN);
        int vh = Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN);

        var source = PresentationSource.FromVisual(this);
        double scaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
        if (scaleX <= 0) scaleX = 1.0;
        if (scaleY <= 0) scaleY = 1.0;

        Left = vx / scaleX;
        Top = vy / scaleY;
        Width = vw / scaleX;
        Height = vh / scaleY;
    }

    private (double X, double Y) ScreenToCanvasDip(double screenX, double screenY)
    {
        var source = PresentationSource.FromVisual(this);
        double scaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
        if (scaleX <= 0) scaleX = 1.0;
        if (scaleY <= 0) scaleY = 1.0;

        int vx = Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN);
        int vy = Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN);

        return ((screenX - vx) / scaleX, (screenY - vy) / scaleY);
    }

    public void ShowVisualAction(VisualActionState state)
    {
        if (_isDisposed) return;
        EnsureVisible();

        if (!string.IsNullOrWhiteSpace(state.Description))
        {
            _hudDetailText.Text = state.Description;
        }

        var (targetX, targetY) = ScreenToCanvasDip(state.X, state.Y);

        switch (state.Type)
        {
            case "click":
                AnimateCursorTo(targetX, targetY, 65);
                PlayClickTactile(targetX, targetY);
                HideDragElements();
                _cursorBadge.Visibility = Visibility.Collapsed;
                break;

            case "type":
                if (state.X > 0 || state.Y > 0)
                {
                    AnimateCursorTo(targetX, targetY, 65);
                    Canvas.SetLeft(_cursorBadge, targetX + 14);
                    Canvas.SetTop(_cursorBadge, targetY - 12);
                }
                else if (_lastX > 0 && _lastY > 0)
                {
                    Canvas.SetLeft(_cursorBadge, _lastX + 14);
                    Canvas.SetTop(_cursorBadge, _lastY - 12);
                }
                HideDragElements();
                _cursorBadgeText.Text = "⌨ Yazılıyor";
                _cursorBadge.Visibility = Visibility.Visible;
                break;

            case "key":
                _cursorBadgeText.Text = "⌨ Kısayol";
                if (_lastX > 0 && _lastY > 0)
                {
                    Canvas.SetLeft(_cursorBadge, _lastX + 14);
                    Canvas.SetTop(_cursorBadge, _lastY - 12);
                    _cursorBadge.Visibility = Visibility.Visible;
                }
                HideDragElements();
                break;

            case "drag":
                if (state.FromX.HasValue && state.FromY.HasValue && state.ToX.HasValue && state.ToY.HasValue)
                {
                    var (fromDipX, fromDipY) = ScreenToCanvasDip(state.FromX.Value, state.FromY.Value);
                    var (toDipX, toDipY) = ScreenToCanvasDip(state.ToX.Value, state.ToY.Value);

                    _dragStartDot.Visibility = Visibility.Visible;
                    Canvas.SetLeft(_dragStartDot, fromDipX - 5);
                    Canvas.SetTop(_dragStartDot, fromDipY - 5);

                    _dragLine.X1 = fromDipX;
                    _dragLine.Y1 = fromDipY;
                    _dragLine.X2 = toDipX;
                    _dragLine.Y2 = toDipY;
                    _dragLine.Visibility = Visibility.Visible;

                    Canvas.SetLeft(_appleCursor, fromDipX - 11);
                    Canvas.SetTop(_appleCursor, fromDipY - 11);
                    _lastX = fromDipX;
                    _lastY = fromDipY;

                    int dur = Math.Clamp(state.DurationMs, 90, 420);
                    AnimateCursorTo(toDipX, toDipY, dur);

                    _cursorBadgeText.Text = "↔ Sürükle";
                    Canvas.SetLeft(_cursorBadge, toDipX + 14);
                    Canvas.SetTop(_cursorBadge, toDipY - 12);
                    _cursorBadge.Visibility = Visibility.Visible;
                }
                break;

            case "scroll":
                AnimateCursorTo(targetX, targetY, 65);
                HideDragElements();
                _cursorBadgeText.Text = "⇅ Kaydır";
                Canvas.SetLeft(_cursorBadge, targetX + 14);
                Canvas.SetTop(_cursorBadge, targetY - 12);
                _cursorBadge.Visibility = Visibility.Visible;
                break;

            case "hover":
            case "move":
            default:
                AnimateCursorTo(targetX, targetY, 35);
                HideDragElements();
                _cursorBadge.Visibility = Visibility.Collapsed;
                break;
        }

        RestartAutoHideTimer();
    }

    private void AnimateCursorTo(double dipX, double dipY, int durationMs = 65)
    {
        double destLeft = dipX - 11;
        double destTop = dipY - 11;

        if (_lastX < 0 || _lastY < 0)
        {
            Canvas.SetLeft(_appleCursor, destLeft);
            Canvas.SetTop(_appleCursor, destTop);
            _lastX = dipX;
            _lastY = dipY;
            return;
        }

        double dx = dipX - _lastX;
        double dy = dipY - _lastY;
        double dist = Math.Sqrt(dx * dx + dy * dy);

        // Draw subtle fading motion trail when gliding across screen
        if (dist > 18 && dist < 1600)
        {
            _motionTrailLine.X1 = _lastX;
            _motionTrailLine.Y1 = _lastY;
            _motionTrailLine.X2 = dipX;
            _motionTrailLine.Y2 = dipY;
            _motionTrailLine.Visibility = Visibility.Visible;

            var trailFade = new DoubleAnimation
            {
                From = 0.75,
                To = 0.0,
                Duration = TimeSpan.FromMilliseconds(220),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            _motionTrailLine.BeginAnimation(UIElement.OpacityProperty, trailFade);
        }

        if (dist > 1800 || durationMs <= 25)
        {
            _appleCursor.BeginAnimation(Canvas.LeftProperty, null);
            _appleCursor.BeginAnimation(Canvas.TopProperty, null);
            Canvas.SetLeft(_appleCursor, destLeft);
            Canvas.SetTop(_appleCursor, destTop);
        }
        else
        {
            var animLeft = new DoubleAnimation
            {
                To = destLeft,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var animTop = new DoubleAnimation
            {
                To = destTop,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            _appleCursor.BeginAnimation(Canvas.LeftProperty, animLeft);
            _appleCursor.BeginAnimation(Canvas.TopProperty, animTop);
        }

        _lastX = dipX;
        _lastY = dipY;
    }

    private void PlayClickTactile(double dipX, double dipY)
    {
        var squeezeAnim = new DoubleAnimation
        {
            To = 0.76,
            Duration = TimeSpan.FromMilliseconds(65),
            AutoReverse = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        _cursorScale.BeginAnimation(ScaleTransform.ScaleXProperty, squeezeAnim);
        _cursorScale.BeginAnimation(ScaleTransform.ScaleYProperty, squeezeAnim);

        // Inner cyan wave
        Canvas.SetLeft(_clickRipple, dipX - 12);
        Canvas.SetTop(_clickRipple, dipY - 12);
        _clickRipple.Visibility = Visibility.Visible;

        var rippleScaleAnim = new DoubleAnimation
        {
            From = 0.85,
            To = 2.6,
            Duration = TimeSpan.FromMilliseconds(380),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var rippleFadeAnim = new DoubleAnimation
        {
            From = 0.95,
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(380),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        rippleFadeAnim.Completed += (_, _) => _clickRipple.Visibility = Visibility.Collapsed;

        _rippleScale.BeginAnimation(ScaleTransform.ScaleXProperty, rippleScaleAnim);
        _rippleScale.BeginAnimation(ScaleTransform.ScaleYProperty, rippleScaleAnim);
        _clickRipple.BeginAnimation(UIElement.OpacityProperty, rippleFadeAnim);

        // Outer violet wave
        Canvas.SetLeft(_clickRippleOuter, dipX - 14);
        Canvas.SetTop(_clickRippleOuter, dipY - 14);
        _clickRippleOuter.Visibility = Visibility.Visible;

        var outerScaleAnim = new DoubleAnimation
        {
            From = 0.9,
            To = 3.3,
            Duration = TimeSpan.FromMilliseconds(460),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var outerFadeAnim = new DoubleAnimation
        {
            From = 0.75,
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(460),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        outerFadeAnim.Completed += (_, _) => _clickRippleOuter.Visibility = Visibility.Collapsed;

        _rippleOuterScale.BeginAnimation(ScaleTransform.ScaleXProperty, outerScaleAnim);
        _rippleOuterScale.BeginAnimation(ScaleTransform.ScaleYProperty, outerScaleAnim);
        _clickRippleOuter.BeginAnimation(UIElement.OpacityProperty, outerFadeAnim);
    }

    public void UpdateTargetBounds(Win32.RECT? bounds)
    {
        // Intentionally no-op: never draw rectangular frames or colored boxes on the user's screen.
        if (_isDisposed || !bounds.HasValue) return;
        EnsureVisible();
        RestartAutoHideTimer();
    }

    private void EnsureVisible()
    {
        if (!IsVisible) Show();
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            Win32.SetWindowPos(
                hwnd,
                Win32.HWND_TOPMOST,
                0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
        }
        if (Opacity < 0.99)
        {
            var fadeIn = new DoubleAnimation
            {
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(140),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            BeginAnimation(UIElement.OpacityProperty, fadeIn);
        }
    }

    private void FadeOut()
    {
        _autoHideTimer.Stop();
        var fadeOut = new DoubleAnimation
        {
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(350),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        fadeOut.Completed += (_, _) =>
        {
            HideDragElements();
            _motionTrailLine.Visibility = Visibility.Collapsed;
            _cursorBadge.Visibility = Visibility.Collapsed;
            _lastX = -100;
            _lastY = -100;
        };
        BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    public void DismissImmediate()
    {
        _autoHideTimer.Stop();
        BeginAnimation(UIElement.OpacityProperty, null);
        Opacity = 0.0;
        HideDragElements();
        _motionTrailLine.Visibility = Visibility.Collapsed;
        _cursorBadge.Visibility = Visibility.Collapsed;
        _lastX = -100;
        _lastY = -100;
    }

    private void HideDragElements()
    {
        _dragLine.Visibility = Visibility.Collapsed;
        _dragStartDot.Visibility = Visibility.Collapsed;
    }

    private void RestartAutoHideTimer()
    {
        _autoHideTimer.Stop();
        _autoHideTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _isDisposed = true;
        _autoHideTimer.Stop();
        base.OnClosed(e);
    }
}

/// <summary>
/// Non-activating Turkish WPF confirmation dialog with 60-second timeout (§7.4).
/// </summary>
public class ConfirmDialogWindow : Window
{
    public ConfirmResult Result { get; private set; } = ConfirmResult.Deny;
    private readonly DispatcherTimer _timer;
    private int _secondsLeft = 60;
    private readonly System.Windows.Controls.Button _denyBtn;

    public ConfirmDialogWindow(
        string clientName,
        string tool,
        string risk,
        List<string> reasons,
        string paramsSummary,
        bool allowSession)
    {
        Title = "Prodesk Güvenlik Onayı";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = SystemParameters.WorkArea.Right - 480;
        Top = SystemParameters.WorkArea.Bottom - 340;
        Topmost = true;
        ShowActivated = false; // Do not steal keyboard focus from the user (§7.4)
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(15, 23, 42));
        Foreground = Brushes.White;
        FontFamily = new FontFamily("Segoe UI");

        var stack = new StackPanel { Margin = new Thickness(18) };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        header.Children.Add(new TextBlock
        {
            Text = $"Onay Gerekiyor — {tool}",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White
        });
        header.Children.Add(new Border
        {
            Background = risk == "critical"
                ? new SolidColorBrush(Color.FromRgb(220, 38, 38))
                : new SolidColorBrush(Color.FromRgb(217, 119, 6)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(10, 0, 0, 0),
            Child = new TextBlock { Text = risk.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.Bold }
        });
        stack.Children.Add(header);

        stack.Children.Add(new TextBlock
        {
            Text = $"İstemci: {clientName}",
            Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 8)
        });

        foreach (var r in reasons)
        {
            stack.Children.Add(new TextBlock
            {
                Text = $"• {r}",
                Foreground = new SolidColorBrush(Color.FromRgb(252, 211, 77)),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12.5,
                Margin = new Thickness(0, 0, 0, 4)
            });
        }

        stack.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 8, 0, 14),
            Child = new TextBlock
            {
                Text = paramsSummary,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                TextWrapping = TextWrapping.Wrap
            }
        });

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };

        var allowOnceBtn = CreateButton("Bir Kez İzin Ver", Color.FromRgb(16, 185, 129));
        allowOnceBtn.Click += (_, _) => { Result = ConfirmResult.AllowOnce; Close(); };
        btnRow.Children.Add(allowOnceBtn);

        if (allowSession)
        {
            var allowSessBtn = CreateButton("Bu Oturum Boyunca İzin Ver", Color.FromRgb(2, 132, 199));
            allowSessBtn.Margin = new Thickness(8, 0, 0, 0);
            allowSessBtn.Click += (_, _) => { Result = ConfirmResult.AllowSession; Close(); };
            btnRow.Children.Add(allowSessBtn);
        }

        _denyBtn = CreateButton("Reddet (60s)", Color.FromRgb(239, 68, 68));
        _denyBtn.Margin = new Thickness(8, 0, 0, 0);
        _denyBtn.Click += (_, _) => { Result = ConfirmResult.Deny; Close(); };
        btnRow.Children.Add(_denyBtn);

        stack.Children.Add(btnRow);
        Content = stack;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Result = ConfirmResult.AllowOnce; Close(); }
            else if (e.Key == Key.Escape) { Result = ConfirmResult.Deny; Close(); }
        };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            _secondsLeft--;
            _denyBtn.Content = $"Reddet ({_secondsLeft}s)";
            if (_secondsLeft <= 0)
            {
                _timer.Stop();
                Result = ConfirmResult.Deny;
                Close();
            }
        };
        _timer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }

    private static System.Windows.Controls.Button CreateButton(string text, Color bg)
    {
        return new System.Windows.Controls.Button
        {
            Content = text,
            Background = new SolidColorBrush(bg),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12, 7, 12, 7),
            FontWeight = FontWeights.SemiBold,
            Cursor = System.Windows.Input.Cursors.Hand
        };
    }
}

/// <summary>
/// Clean, modern Windows 11-style Turkish Settings Window with 4 tabs: Genel, Engeller, Kısayollar, Kayıt (§7.8).
/// </summary>
public class SettingsDialogWindow : Window
{
    private readonly PolicyManager _policy;

    public SettingsDialogWindow(PolicyManager policy)
    {
        _policy = policy;
        Title = "Prodesk Ayarları";
        Width = 640;
        Height = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(15, 23, 42));
        Foreground = Brushes.White;
        FontFamily = new FontFamily("Segoe UI");

        var tabs = new System.Windows.Controls.TabControl
        {
            Background = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
            Foreground = Brushes.White,
            Margin = new Thickness(14)
        };

        tabs.Items.Add(BuildGeneralTab());
        tabs.Items.Add(BuildBlocksTab());
        tabs.Items.Add(BuildHotkeysTab());
        tabs.Items.Add(BuildAuditTab());

        Content = tabs;
    }

    private TabItem BuildGeneralTab()
    {
        var panel = new StackPanel { Margin = new Thickness(16) };

        var pauseChk = CreateCheckBox("Otomasyonu Duraklat", _policy.Settings.Paused, val => _policy.SetPaused(val));
        var autoChk = CreateCheckBox("Soru sormadan otomatik onayla (Auto-Approve)", _policy.Settings.AutoApprove, val => _policy.SetAutoApprove(val));
        var unsignedChk = CreateCheckBox("İmzasız yükleyicileri de otomatik çalıştır", _policy.Settings.AllowUnsignedInstallers, val =>
        {
            _policy.Settings.AllowUnsignedInstallers = val;
            _policy.SaveSettings();
        });
        var keepAliveChk = CreateCheckBox("Helper sürecini her zaman açık tut", _policy.Settings.KeepHelperAlive, val =>
        {
            _policy.Settings.KeepHelperAlive = val;
            _policy.SaveSettings();
        });

        panel.Children.Add(pauseChk);
        panel.Children.Add(autoChk);
        panel.Children.Add(unsignedChk);
        panel.Children.Add(keepAliveChk);

        var idleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        idleRow.Children.Add(new TextBlock { Text = "Kullanıcı boşta bekleme süresi (ms):", Width = 240, VerticalAlignment = VerticalAlignment.Center });
        var idleBox = new System.Windows.Controls.TextBox
        {
            Text = _policy.Settings.WaitForUserIdleMs.ToString(),
            Width = 90,
            Padding = new Thickness(6, 4, 6, 4)
        };
        idleBox.LostFocus += (_, _) =>
        {
            if (int.TryParse(idleBox.Text, out int v))
            {
                _policy.Settings.WaitForUserIdleMs = Math.Clamp(v, 0, 10000);
                _policy.SaveSettings();
            }
        };
        idleRow.Children.Add(idleBox);
        panel.Children.Add(idleRow);

        var rateRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        rateRow.Children.Add(new TextBlock { Text = "Dakikalık yazma eylemi sınırı:", Width = 240, VerticalAlignment = VerticalAlignment.Center });
        var rateBox = new System.Windows.Controls.TextBox
        {
            Text = _policy.Settings.WritesPerMinute.ToString(),
            Width = 90,
            Padding = new Thickness(6, 4, 6, 4)
        };
        rateBox.LostFocus += (_, _) =>
        {
            if (int.TryParse(rateBox.Text, out int v))
            {
                _policy.Settings.WritesPerMinute = Math.Clamp(v, 10, 1000);
                _policy.SaveSettings();
            }
        };
        rateRow.Children.Add(rateBox);
        panel.Children.Add(rateRow);

        return new TabItem { Header = "Genel", Content = panel };
    }

    private TabItem BuildBlocksTab()
    {
        var panel = new DockPanel { Margin = new Thickness(16) };
        var listBox = new System.Windows.Controls.ListBox
        {
            Height = 260,
            Background = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
            Foreground = Brushes.White
        };

        void RefreshList()
        {
            listBox.Items.Clear();
            foreach (var r in _policy.Settings.AppRules)
            {
                listBox.Items.Add($"{r.Pattern} [{r.Mode}] — {r.Reason}");
            }
        }
        RefreshList();

        var bottomRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var patternInput = new System.Windows.Controls.TextBox { Width = 180, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(6, 4, 6, 4) };
        var addBtn = new System.Windows.Controls.Button { Content = "Ekle", Padding = new Thickness(12, 4, 12, 4) };
        addBtn.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(patternInput.Text))
            {
                _policy.Settings.AppRules.Add(new AppRuleDto
                {
                    Pattern = patternInput.Text.Trim(),
                    Mode = "block",
                    Reason = "Kullanıcı kuralı"
                });
                _policy.SaveSettings();
                patternInput.Text = "";
                RefreshList();
            }
        };

        var removeBtn = new System.Windows.Controls.Button { Content = "Seçileni Kaldır", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 4, 12, 4) };
        removeBtn.Click += (_, _) =>
        {
            int idx = listBox.SelectedIndex;
            if (idx >= 0 && idx < _policy.Settings.AppRules.Count)
            {
                _policy.Settings.AppRules.RemoveAt(idx);
                _policy.SaveSettings();
                RefreshList();
            }
        };

        var resetBtn = new System.Windows.Controls.Button { Content = "Varsayılana Dön", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 4, 12, 4) };
        resetBtn.Click += (_, _) =>
        {
            _policy.Settings.AppRules = HelperSettings.DefaultAppRules();
            _policy.SaveSettings();
            RefreshList();
        };

        bottomRow.Children.Add(patternInput);
        bottomRow.Children.Add(addBtn);
        bottomRow.Children.Add(removeBtn);
        bottomRow.Children.Add(resetBtn);

        DockPanel.SetDock(bottomRow, Dock.Bottom);
        panel.Children.Add(bottomRow);
        panel.Children.Add(listBox);

        return new TabItem { Header = "Engeller", Content = panel };
    }

    private TabItem BuildHotkeysTab()
    {
        var panel = new StackPanel { Margin = new Thickness(16) };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = "Acil Durdurma Kısayolu:", Width = 200, VerticalAlignment = VerticalAlignment.Center });
        var box = new System.Windows.Controls.TextBox
        {
            Text = _policy.Settings.EmergencyHotkey,
            Width = 180,
            Padding = new Thickness(6, 4, 6, 4),
            IsReadOnly = true
        };
        row.Children.Add(box);
        panel.Children.Add(row);
        return new TabItem { Header = "Kısayollar", Content = panel };
    }

    private TabItem BuildAuditTab()
    {
        var panel = new DockPanel { Margin = new Thickness(16) };
        var topRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var filterBox = new System.Windows.Controls.TextBox { Width = 240, Padding = new Thickness(6, 4, 6, 4) };
        var refreshBtn = new System.Windows.Controls.Button { Content = "Yenile", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 4, 12, 4) };
        var exportBtn = new System.Windows.Controls.Button { Content = "JSON Dışa Aktar", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 4, 12, 4) };

        var listBox = new System.Windows.Controls.ListBox
        {
            Background = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11.5
        };

        void LoadAudit()
        {
            listBox.Items.Clear();
            string q = filterBox.Text.Trim();
            foreach (var e in _policy.ReadRecentAuditEntries(200))
            {
                string line = $"{e.Timestamp[11..19]} [{e.Decision.ToUpperInvariant()}] {e.Tool} ({e.Risk}) {e.TargetApp ?? ""} {e.DurationMs}ms {e.Error ?? ""}";
                if (string.IsNullOrEmpty(q) || line.Contains(q, StringComparison.OrdinalIgnoreCase))
                {
                    listBox.Items.Add(line);
                }
            }
        }

        refreshBtn.Click += (_, _) => LoadAudit();
        filterBox.TextChanged += (_, _) => LoadAudit();
        exportBtn.Click += (_, _) =>
        {
            var entries = _policy.ReadRecentAuditEntries(500);
            string outFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), $"prodesk-audit-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(outFile, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
            System.Windows.MessageBox.Show($"Kayıtlar dışa aktarıldı:\n{outFile}", "Prodesk", MessageBoxButton.OK, MessageBoxImage.Information);
        };

        topRow.Children.Add(filterBox);
        topRow.Children.Add(refreshBtn);
        topRow.Children.Add(exportBtn);

        DockPanel.SetDock(topRow, Dock.Top);
        panel.Children.Add(topRow);
        panel.Children.Add(listBox);

        LoadAudit();
        return new TabItem { Header = "Kayıt", Content = panel };
    }

    private static System.Windows.Controls.CheckBox CreateCheckBox(string label, bool initial, Action<bool> onChanged)
    {
        var chk = new System.Windows.Controls.CheckBox
        {
            Content = label,
            IsChecked = initial,
            Foreground = Brushes.White,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 10)
        };
        chk.Checked += (_, _) => onChanged(true);
        chk.Unchecked += (_, _) => onChanged(false);
        return chk;
    }
}

/// <summary>
/// Native WPF Calculator window with standard Windows Calculator UIA AutomationIds,
/// used as an automatic fallback when UWP ApplicationFrameHost is unavailable on non-Default Win32 desktops.
/// </summary>
public class NativeCalculatorWindow : Window
{
    private string _currentInput = "0";
    private double? _acc = null;
    private string? _pendingOp = null;
    private bool _resetNext = false;
    private readonly TextBlock _displayBlock;

    public NativeCalculatorWindow()
    {
        Title = "Hesap Makinesi";
        Width = 320;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = 120;
        Top = 120;
        ShowActivated = false;
        Background = new SolidColorBrush(Color.FromRgb(32, 32, 32));
        Foreground = Brushes.White;
        FontFamily = new FontFamily("Segoe UI");

        var root = new StackPanel { Margin = new Thickness(12) };

        _displayBlock = new TextBlock
        {
            Text = "Ekran değeri 0",
            FontSize = 24,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            Margin = new Thickness(4, 8, 4, 14)
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_displayBlock, "CalculatorResults");
        System.Windows.Automation.AutomationProperties.SetName(_displayBlock, "Ekran değeri 0");
        root.Children.Add(_displayBlock);

        var grid = new System.Windows.Controls.Primitives.UniformGrid { Rows = 4, Columns = 4 };

        void UpdateDisplay(string val)
        {
            _currentInput = val;
            string label = $"Ekran değeri {val}";
            _displayBlock.Text = label;
            System.Windows.Automation.AutomationProperties.SetName(_displayBlock, label);
        }

        void OnDigit(int d)
        {
            if (_resetNext || _currentInput == "0")
            {
                UpdateDisplay(d.ToString());
                _resetNext = false;
            }
            else
            {
                UpdateDisplay(_currentInput + d);
            }
        }

        void OnOp(string op)
        {
            if (double.TryParse(_currentInput, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double cur))
            {
                if (_acc.HasValue && _pendingOp != null && !_resetNext)
                {
                    _acc = ApplyOp(_acc.Value, cur, _pendingOp);
                    UpdateDisplay(_acc.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                else
                {
                    _acc = cur;
                }
            }
            _pendingOp = op;
            _resetNext = true;
        }

        void OnEquals()
        {
            if (_acc.HasValue && _pendingOp != null &&
                double.TryParse(_currentInput, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double cur))
            {
                double res = ApplyOp(_acc.Value, cur, _pendingOp);
                _acc = null;
                _pendingOp = null;
                _resetNext = true;
                UpdateDisplay(res.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        void AddBtn(string label, string autoId, Action onClick)
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label,
                Margin = new Thickness(3),
                Padding = new Thickness(8),
                FontSize = 16
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(btn, autoId);
            System.Windows.Automation.AutomationProperties.SetName(btn, label);
            btn.Click += (_, _) => onClick();
            grid.Children.Add(btn);
        }

        AddBtn("7", "num7Button", () => OnDigit(7));
        AddBtn("8", "num8Button", () => OnDigit(8));
        AddBtn("9", "num9Button", () => OnDigit(9));
        AddBtn("×", "multiplyButton", () => OnOp("*"));
        AddBtn("4", "num4Button", () => OnDigit(4));
        AddBtn("5", "num5Button", () => OnDigit(5));
        AddBtn("6", "num6Button", () => OnDigit(6));
        AddBtn("-", "minusButton", () => OnOp("-"));
        AddBtn("1", "num1Button", () => OnDigit(1));
        AddBtn("2", "num2Button", () => OnDigit(2));
        AddBtn("3", "num3Button", () => OnDigit(3));
        AddBtn("+", "plusButton", () => OnOp("+"));
        AddBtn("C", "clearButton", () => { _acc = null; _pendingOp = null; UpdateDisplay("0"); });
        AddBtn("0", "num0Button", () => OnDigit(0));
        AddBtn("÷", "divideButton", () => OnOp("/"));
        AddBtn("=", "equalButton", OnEquals);

        root.Children.Add(grid);
        Content = root;
    }

    private static double ApplyOp(double a, double b, string op) => op switch
    {
        "+" => a + b,
        "-" => a - b,
        "*" => a * b,
        "/" => b != 0 ? a / b : 0,
        _ => b
    };
}
