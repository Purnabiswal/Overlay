using System.Drawing;
using System.Drawing.Drawing2D;
using System.Diagnostics;
using System.IO;

namespace MyOverlay;

public class OverlayForm : Form
{
    private const int HOTKEY_CLICK_THROUGH = 1001;
    private const int HOTKEY_SETTINGS = 1002;
    private const int HOTKEY_VISIBILITY = 1003;
    private const int HOTKEY_TOGGLE_BROWSER = 1004;
    private const int HOTKEY_TRANSCRIPTION = 1005;
    private const int HOTKEY_TOGGLE_TOPMOST = 1006;

    // Keep the overlay at the top of the topmost Z-order band. Some apps
    // repeatedly promote their own topmost window, so reassert our position.
    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOOWNERZORDER = 0x0200;
    private static readonly uint TOPMOST_FLAGS =
        SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE | SWP_NOOWNERZORDER;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    // Ofradr's ordinary Z-order approach reasserts HWND_TOPMOST from its render
    // loop. Use a small background interval here so a busy WinForms/WebView2 UI
    // thread does not prevent MyOverlay from reasserting its own Z-order.
    // Created only after the form's UI has been initialized. A nullable reference lets
    // closing atomically take ownership of disposal while an in-flight callback exits.
    private System.Threading.Timer? _topmostEnforcer;
    private long _topmostWindowHandle;
    private volatile bool _keepTopmost;
    private volatile bool _isClosing;
    private readonly System.Windows.Forms.Timer _targetProcessMonitor = new()
    {
        Interval = 500
    };
    private readonly string _targetConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MyOverlay", "target-executable.txt");
    private string _targetExecutable = string.Empty;
    private bool _targetWindowWasReady;
    private bool _initialTargetPromptShown;
    private DateTime _targetReadySinceUtc;
    private bool _overlayActivationCompleted;
    private static readonly TimeSpan TargetLaunchDelay = TimeSpan.FromSeconds(20);
    // The overlay must be visible on normal application startup. Target monitoring can
    // still deliberately hide it after the user explicitly configures a target app.
    private bool _allowOverlayVisible = true;
    private bool _restoredBoundsChanged;
    private readonly HashSet<int> _registeredHotkeys = new();
    private IntPtr _hotkeyWindowHandle;

    private readonly AppSettings _settings;
    private readonly AudioCaptureManager _audioManager;
    private readonly AudioTranscriptionService _transcriptionService;
    private NotifyIcon _notifyIcon = null!;
    private ContextMenuStrip _trayContextMenu = null!;
    private ContextMenuStrip _overlayContextMenu = null!;
    private SettingsForm? _settingsForm;

    public AudioCaptureManager AudioManager => _audioManager;
    public AudioTranscriptionService TranscriptionService => _transcriptionService;
    public BrowserPanel BrowserPanel => _browserPanel;

    // Header & Controls
    private Panel _pnlHeader = null!;
    private Label _lblTitle = null!;
    private Label _lblAudioStatus = null!;
    private Button _btnTranscription = null!;
    private Button _btnToggleBrowser = null!;
    private Button _btnCaptureScreenshot = null!;
    private Button _btnConfigureChatTarget = null!;
    private Button _btnSettings = null!;
    private Button _btnMinimize = null!;
    private Button _btnClose = null!;

    // Content panels
    private Panel _pnlContentHost = null!;
    private Panel _pnlScreenshotTools = null!;
    private BrowserPanel _browserPanel = null!;
    private ScreenshotSidebar _screenshotSidebar = null!;
    private Splitter _sidebarSplitter = null!;
    private Panel _notesPanel = null!;
    private Label _lblNotes = null!;
    private bool _capturingChatTarget;
    private bool _sidebarVisible = true;

    private Icon? _appIcon;

    public OverlayForm()
    {
        _settings = AppSettings.Load();
        _audioManager = new AudioCaptureManager();
        _transcriptionService = new AudioTranscriptionService(_audioManager, () => _browserPanel, _settings);
        _transcriptionService.NotificationRequested += OnTranscriptionNotification;
        _transcriptionService.StateChanged += OnTranscriptionStateChanged;
        _transcriptionService.AudioLevelChanged += OnTranscriptionAudioLevelChanged;

        InitializeFormProperties();
        InitializeHeader();
        InitializeContentPanels();
        InitializeTrayIcon();
        InitializeContextMenus();

        ApplySettings(_settings);
        if (_restoredBoundsChanged)
            _settings.Save();
        LoadTargetExecutable();

        _targetProcessMonitor.Tick += (_, _) => CheckTargetApplication();
        _targetProcessMonitor.Start();
        // The timer is deliberately initialized last. Its state is this form (not null),
        // and it remains inactive until the complete constructor has established state.
        _topmostEnforcer = new System.Threading.Timer(
            static state =>
            {
                if (state is OverlayForm form)
                    form.EnforceTopMostFromWorker();
            },
            this,
            System.Threading.Timeout.Infinite,
            System.Threading.Timeout.Infinite);

        // Reassert Z-order without activating the overlay or stealing keyboard focus.
        _topmostEnforcer.Change(dueTime: 0, period: 16);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // ToolWindow extended style excludes window from Alt+Tab switcher and ordinary window-sharing pickers
            cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW;
            // Toolbar/sidebar clicks are explicitly declined in WM_MOUSEACTIVATE below. WebView
            // clicks remain activatable so Chromium can receive keyboard input when requested.
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            return cp;
        }
    }

    private void InitializeFormProperties()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        KeyPreview = true;
        Padding = new Padding(4); // 4px margin allows edge resizing via WM_NCHITTEST

        SetStyle(
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.ResizeRedraw,
            true
        );

        UpdateStyles();

        // Hook lifecycle events to maintain capture exclusion across all states
        ScreenCaptureProtection.ProtectForm(this);
    }

    private void InitializeHeader()
    {
        _pnlHeader = new Panel
        {
            Dock = DockStyle.Top,
            Height = 28,
            BackColor = Color.FromArgb(20, 20, 26),
            Padding = new Padding(6, 2, 6, 2)
        };

        _pnlHeader.MouseDown += (s, e) =>
        {
            if (e.Button == MouseButtons.Left && !_settings.ClickThrough)
            {
                DragWindow();
            }
        };

        _lblTitle = new Label
        {
            Text = "⚡ MyOverlay",
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            ForeColor = Color.FromArgb(165, 180, 252),
            AutoSize = true,
            Location = new Point(8, 6),
            Cursor = Cursors.SizeAll
        };
        _lblTitle.MouseDown += (s, e) =>
        {
            if (e.Button == MouseButtons.Left && !_settings.ClickThrough)
            {
                DragWindow();
            }
        };

        _lblAudioStatus = new Label
        {
            Text = "⚪ Audio: Ready",
            Font = new Font("Segoe UI", 7.5f, FontStyle.Regular),
            ForeColor = Color.FromArgb(156, 163, 175),
            BackColor = Color.FromArgb(28, 28, 35),
            TextAlign = ContentAlignment.MiddleCenter,
            AutoSize = false,
            Height = 22,
            Width = 110,
            Cursor = Cursors.Hand
        };
        _lblAudioStatus.Click += (s, e) => OpenSettings();

        _btnTranscription = CreateHeaderButton(
            "🎙️ Transcribe",
            "Toggle Continuous Audio-to-Text into ChatGPT / Gemini (Ctrl+Alt+T)",
            (s, e) => ToggleTranscription()
        );
        _btnTranscription.Width = 100;

        _btnToggleBrowser = CreateHeaderButton(
            _settings.ShowBrowser ? "📝 Notes" : "🌐 Browser",
            "Toggle Browser Panel (Ctrl+B)",
            (s, e) => ToggleBrowserPanel()
        );
        _btnToggleBrowser.Width = 75;

        _btnCaptureScreenshot = CreateHeaderButton("✂", "Capture screen and paste into ChatGPT composer", async (s, e) => await CaptureScreenshotAsync());
        _btnConfigureChatTarget = CreateHeaderButton("◎", "Set target / show screenshot sidebar", (s, e) =>
        {
            if (!_sidebarVisible) SetSidebarVisible(true);
            else BeginChatTargetCapture();
        });

        _btnSettings = CreateHeaderButton("⚙️", "Settings (Ctrl+Alt+S)", (s, e) => OpenSettings());
        _btnMinimize = CreateHeaderButton("─", "Hide Overlay (Ctrl+Alt+H)", (s, e) => ToggleVisibility());
        _btnClose = CreateHeaderButton("✕", "Exit Application", (s, e) => ExitApplication());
        _btnClose.FlatAppearance.MouseOverBackColor = Color.FromArgb(239, 68, 68);

        _pnlHeader.Controls.Add(_lblTitle);
        _pnlHeader.Controls.Add(_lblAudioStatus);
        _pnlHeader.Controls.Add(_btnTranscription);
        _pnlHeader.Controls.Add(_btnToggleBrowser);
        _pnlHeader.Controls.Add(_btnCaptureScreenshot);
        _pnlHeader.Controls.Add(_btnConfigureChatTarget);
        _pnlHeader.Controls.Add(_btnSettings);
        _pnlHeader.Controls.Add(_btnMinimize);
        _pnlHeader.Controls.Add(_btnClose);

        _pnlHeader.Resize += (s, e) => RepositionHeaderControls();
        RepositionHeaderControls();

        Controls.Add(_pnlHeader);
    }

    private void RepositionHeaderControls()
    {
        int right = _pnlHeader.ClientSize.Width - 4;

        _btnClose.Location = new Point(right - 24, 2);
        _btnMinimize.Location = new Point(right - 50, 2);
        _btnSettings.Location = new Point(right - 78, 2);
        _btnToggleBrowser.Location = new Point(right - 158, 2);
        _btnCaptureScreenshot.Location = new Point(right - 186, 2);
        _btnConfigureChatTarget.Location = new Point(right - 214, 2);
        _btnTranscription.Location = new Point(right - 318, 2);
        _lblAudioStatus.Location = new Point(right - 432, 3);
    }

    private void InitializeContentPanels()
    {
        _pnlContentHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(24, 24, 27)
        };

        // 1. Browser Panel
        _browserPanel = new BrowserPanel(_settings)
        {
            Dock = DockStyle.Fill,
            Visible = _settings.ShowBrowser
        };
        _browserPanel.RequestDragWindow += DragWindow;
        _browserPanel.ShortcutTriggered += HandleCustomShortcut;
        _browserPanel.ActiveWebViewPointerDown += OnActiveWebViewPointerDown;
        _browserPanel.ActiveWebViewEscapePressed += CancelChatTargetCapture;

        // Compact left rail keeps the two primary screenshot actions immediately available.
        _pnlScreenshotTools = new Panel { Dock = DockStyle.Left, Width = 42, BackColor = Color.FromArgb(31, 30, 49), Padding = new Padding(6, 8, 6, 6) };
        var captureRailButton = CreateHeaderButton("✂", "Capture screen", async (s, e) => await CaptureScreenshotAsync());
        captureRailButton.Location = new Point(7, 8);
        var targetRailButton = CreateHeaderButton("◎", "Set target / show sidebar", (s, e) => { if (!_sidebarVisible) SetSidebarVisible(true); else BeginChatTargetCapture(); });
        targetRailButton.Location = new Point(7, 36);
        _pnlScreenshotTools.Controls.Add(captureRailButton);
        _pnlScreenshotTools.Controls.Add(targetRailButton);

        _screenshotSidebar = new ScreenshotSidebar();
        _screenshotSidebar.CaptureRequested += async (_, _) => await CaptureScreenshotAsync();
        _screenshotSidebar.ConfigureTargetRequested += (_, _) => BeginChatTargetCapture();
        _screenshotSidebar.ClearTargetRequested += (_, _) => ClearChatTarget();
        _screenshotSidebar.InsertSelectedRequested += async item => await InsertScreenshotIntoComposerAsync(item.Image);
        _screenshotSidebar.CollapseRequested += (_, _) => SetSidebarVisible(false);
        _screenshotSidebar.SetStatus(HasChatTarget ? "Target: saved (WebView-relative)" : "Target: not set — use ◎ Set target");
        _sidebarSplitter = new Splitter { Dock = DockStyle.Right, Width = 5, MinExtra = 260, MinSize = 180, BackColor = Color.FromArgb(99, 102, 241) };

        // 2. Notes Panel
        _notesPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = _settings.GetBackgroundColor(),
            Padding = new Padding(12),
            Visible = !_settings.ShowBrowser
        };
        _notesPanel.MouseDown += (s, e) =>
        {
            if (e.Button == MouseButtons.Left && !_settings.ClickThrough)
            {
                DragWindow();
            }
        };

        _lblNotes = new Label
        {
            Dock = DockStyle.Fill,
            Font = _settings.Font,
            ForeColor = _settings.GetTextColor(),
            BackColor = Color.Transparent,
            Text = _settings.Text,
            Cursor = Cursors.Default
        };
        _lblNotes.MouseDown += (s, e) =>
        {
            if (e.Button == MouseButtons.Left && !_settings.ClickThrough)
            {
                DragWindow();
            }
        };

        _notesPanel.Controls.Add(_lblNotes);

        _pnlContentHost.Controls.Add(_browserPanel);
        _pnlContentHost.Controls.Add(_sidebarSplitter);
        _pnlContentHost.Controls.Add(_screenshotSidebar);
        _pnlContentHost.Controls.Add(_pnlScreenshotTools);
        _pnlContentHost.Controls.Add(_notesPanel);
        Controls.Add(_pnlContentHost);

        _pnlHeader.BringToFront();
        _pnlContentHost.BringToFront();
    }

    private void InitializeTrayIcon()
    {
        _appIcon = CreateAppIcon();
        Icon = _appIcon;

        _trayContextMenu = new ContextMenuStrip();

        var headerItem = new ToolStripMenuItem("MyOverlay")
        {
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Enabled = false
        };
        _trayContextMenu.Items.Add(headerItem);
        _trayContextMenu.Items.Add(new ToolStripSeparator());

        var itemBrowser = new ToolStripMenuItem("Toggle Browser Panel (Ctrl+B)", null, (s, e) => ToggleBrowserPanel());
        var itemTranscription = new ToolStripMenuItem("Continuous Audio-to-Text (Ctrl+Alt+T)", null, (s, e) => ToggleTranscription());
        var itemAudioCapture = new ToolStripMenuItem("Capture System Audio", null, (s, e) => ToggleSystemAudioCapture());
        var itemRouteAudio = new ToolStripMenuItem("Route Audio to Virtual Mic (WebView2)", null, (s, e) => ToggleVirtualAudioRoute());
        var itemSettings = new ToolStripMenuItem("Settings... (Ctrl+Alt+S)", null, (s, e) => OpenSettings());
        var itemClickThrough = new ToolStripMenuItem("Click-Through (Ctrl+Alt+C)", null, (s, e) => ToggleClickThrough());
        var itemCaptureProtection = new ToolStripMenuItem("Anti-Capture Shield (Zoom/Teams/OBS)", null, (s, e) => ToggleCaptureProtection());
        var itemAlwaysOnTop = new ToolStripMenuItem("Always On Top (Ctrl+Alt+J)", null, (s, e) => ToggleAlwaysOnTop());
        var itemTargetApp = new ToolStripMenuItem("Target application...", null, (s, e) => ConfigureTargetExecutable(showCancel: true));
        var itemCenter = new ToolStripMenuItem("Center Window", null, (s, e) => CenterOnScreen());
        var itemHideShow = new ToolStripMenuItem("Hide / Show (Ctrl+Alt+H)", null, (s, e) => ToggleVisibility());
        var itemExit = new ToolStripMenuItem("Exit", null, (s, e) => ExitApplication());

        _trayContextMenu.Items.Add(itemBrowser);
        _trayContextMenu.Items.Add(itemTranscription);
        _trayContextMenu.Items.Add(itemAudioCapture);
        _trayContextMenu.Items.Add(itemRouteAudio);
        _trayContextMenu.Items.Add(itemSettings);
        _trayContextMenu.Items.Add(itemClickThrough);
        _trayContextMenu.Items.Add(itemCaptureProtection);
        _trayContextMenu.Items.Add(itemAlwaysOnTop);
        _trayContextMenu.Items.Add(itemTargetApp);
        _trayContextMenu.Items.Add(itemCenter);
        _trayContextMenu.Items.Add(new ToolStripSeparator());
        _trayContextMenu.Items.Add(itemHideShow);
        _trayContextMenu.Items.Add(itemExit);

        _trayContextMenu.Opening += (s, e) =>
        {
            itemBrowser.Checked = _settings.ShowBrowser;
            itemTranscription.Checked = _transcriptionService.IsRunning;
            itemAudioCapture.Checked = _settings.CaptureSystemAudio;
            itemRouteAudio.Checked = _settings.RouteToVirtualAudio;
            itemClickThrough.Checked = _settings.ClickThrough;
            itemCaptureProtection.Checked = _settings.ScreenCaptureProtection;
            itemAlwaysOnTop.Checked = _settings.AlwaysOnTop;
        };

        ScreenCaptureProtection.ProtectContextMenu(_trayContextMenu);

        _notifyIcon = new NotifyIcon
        {
            Icon = _appIcon,
            Text = "MyOverlay (Floating Browser & Notes)",
            Visible = true,
            ContextMenuStrip = _trayContextMenu
        };

        _notifyIcon.DoubleClick += (s, e) => OpenSettings();
    }

    private void InitializeContextMenus()
    {
        _overlayContextMenu = new ContextMenuStrip();

        var mnuBrowser = new ToolStripMenuItem("Toggle Browser Panel (Ctrl+B)", null, (s, e) => ToggleBrowserPanel());
        var mnuTranscription = new ToolStripMenuItem("Continuous Audio-to-Text (Ctrl+Alt+T)", null, (s, e) => ToggleTranscription());
        var mnuAudioCapture = new ToolStripMenuItem("Capture System Audio", null, (s, e) => ToggleSystemAudioCapture());
        var mnuRouteAudio = new ToolStripMenuItem("Route Audio to Virtual Mic (WebView2)", null, (s, e) => ToggleVirtualAudioRoute());
        var mnuSettings = new ToolStripMenuItem("Settings... (Ctrl+Alt+S)", null, (s, e) => OpenSettings());
        var mnuClickThrough = new ToolStripMenuItem("Enable Click-Through (Ctrl+Alt+C)", null, (s, e) => ToggleClickThrough());
        var mnuCapture = new ToolStripMenuItem("Anti-Capture Shield", null, (s, e) => ToggleCaptureProtection());
        var mnuAlwaysOnTop = new ToolStripMenuItem("Always On Top (Ctrl+Alt+J)", null, (s, e) => ToggleAlwaysOnTop());
        var mnuTargetApp = new ToolStripMenuItem("Target application...", null, (s, e) => ConfigureTargetExecutable(showCancel: true));
        var mnuCenter = new ToolStripMenuItem("Center Window", null, (s, e) => CenterOnScreen());
        var mnuHide = new ToolStripMenuItem("Hide Overlay (Ctrl+Alt+H)", null, (s, e) => ToggleVisibility());
        var mnuExit = new ToolStripMenuItem("Exit", null, (s, e) => ExitApplication());

        _overlayContextMenu.Items.Add(mnuBrowser);
        _overlayContextMenu.Items.Add(mnuTranscription);
        _overlayContextMenu.Items.Add(mnuAudioCapture);
        _overlayContextMenu.Items.Add(mnuRouteAudio);
        _overlayContextMenu.Items.Add(mnuSettings);
        _overlayContextMenu.Items.Add(mnuClickThrough);
        _overlayContextMenu.Items.Add(mnuCapture);
        _overlayContextMenu.Items.Add(mnuAlwaysOnTop);
        _overlayContextMenu.Items.Add(mnuTargetApp);
        _overlayContextMenu.Items.Add(mnuCenter);
        _overlayContextMenu.Items.Add(new ToolStripSeparator());
        _overlayContextMenu.Items.Add(mnuHide);
        _overlayContextMenu.Items.Add(mnuExit);

        _overlayContextMenu.Opening += (s, e) =>
        {
            mnuTranscription.Checked = _transcriptionService.IsRunning;
            mnuAudioCapture.Checked = _settings.CaptureSystemAudio;
            mnuRouteAudio.Checked = _settings.RouteToVirtualAudio;
            mnuCapture.Checked = _settings.ScreenCaptureProtection;
        };

        ScreenCaptureProtection.ProtectContextMenu(_overlayContextMenu);

        _notesPanel.ContextMenuStrip = _overlayContextMenu;
        _lblNotes.ContextMenuStrip = _overlayContextMenu;
        _pnlHeader.ContextMenuStrip = _overlayContextMenu;
    }

    public void ApplySettings(AppSettings settings)
    {
        RestoreWindowBounds(settings);
        TopMost = settings.AlwaysOnTop;
        _keepTopmost = settings.AlwaysOnTop;
        if (settings.AlwaysOnTop && Visible && IsHandleCreated)
            EnforceTopMost();
        
        // When showing the browser without click-through, Opacity MUST be 1.00 and WS_EX_LAYERED stripped
        // because WebView2's DirectComposition compositor does not support layered windows.
        if (settings.ShowBrowser && !settings.ClickThrough)
        {
            Opacity = 1.00;
        }
        else
        {
            Opacity = Math.Clamp(settings.Opacity, 0.10, 1.00);
        }

        // Update Notes View
        _notesPanel.BackColor = settings.GetBackgroundColor();
        _lblNotes.Font = settings.Font;
        _lblNotes.ForeColor = settings.GetTextColor();
        _lblNotes.Text = settings.Text;

        // Update Browser Visibility
        UpdateBrowserVisibility(settings.ShowBrowser);

        // Update Audio Capture & Transcription Settings
        _audioManager.ApplySettings(settings);
        _transcriptionService.UpdateSettings(settings);
        WebView2AudioPermissionTracker.AutoGrantMicrophone = settings.AutoGrantMicrophonePermission;

        if (IsHandleCreated)
        {
            NativeMethods.SetClickThrough(Handle, settings.ClickThrough, stripLayered: (settings.ShowBrowser || settings.Opacity >= 1.0));
            ScreenCaptureProtection.Enabled = settings.ScreenCaptureProtection;
            ScreenCaptureProtection.ApplyProtection(Handle);
        }

        Invalidate();
    }

    /// <summary>
    /// Restores persisted manual bounds without allowing an unplugged monitor, resolution
    /// change, or corrupt setting to leave the borderless form completely off screen.
    /// </summary>
    private void RestoreWindowBounds(AppSettings settings)
    {
        const int minWidth = 200;
        const int minHeight = 150;
        var requestedSize = new Size(Math.Max(minWidth, settings.Width), Math.Max(minHeight, settings.Height));
        var requestedBounds = new Rectangle(new Point(settings.X, settings.Y), requestedSize);

        bool hasVisibleArea = Screen.AllScreens.Any(screen =>
        {
            Rectangle intersection = Rectangle.Intersect(requestedBounds, screen.WorkingArea);
            return intersection.Width >= 80 && intersection.Height >= 80;
        });

        Rectangle restoredBounds = requestedBounds;
        if (!hasVisibleArea)
        {
            // Screen.FromRectangle selects the nearest current display, including after a
            // monitor has been disconnected. Clamp the entire window into its work area.
            Rectangle workArea = Screen.FromRectangle(requestedBounds).WorkingArea;
            int width = Math.Min(requestedSize.Width, workArea.Width);
            int height = Math.Min(requestedSize.Height, workArea.Height);
            int x = Math.Clamp(requestedBounds.X, workArea.Left, workArea.Right - width);
            int y = Math.Clamp(requestedBounds.Y, workArea.Top, workArea.Bottom - height);
            restoredBounds = new Rectangle(x, y, width, height);
        }

        Bounds = restoredBounds;
        if (settings.X != restoredBounds.X || settings.Y != restoredBounds.Y ||
            settings.Width != restoredBounds.Width || settings.Height != restoredBounds.Height)
        {
            settings.X = restoredBounds.X;
            settings.Y = restoredBounds.Y;
            settings.Width = restoredBounds.Width;
            settings.Height = restoredBounds.Height;
            _restoredBoundsChanged = true;
        }
    }

    public void ToggleBrowserPanel()
    {
        _settings.ShowBrowser = !_settings.ShowBrowser;
        UpdateBrowserVisibility(_settings.ShowBrowser);
        _settings.Save();
    }

    private void UpdateBrowserVisibility(bool showBrowser)
    {
        _browserPanel.Visible = showBrowser;
        _notesPanel.Visible = !showBrowser;
        _btnToggleBrowser.Text = showBrowser ? "📝 Notes" : "🌐 Browser";

        if (showBrowser)
        {
            // CRITICAL: WebView2 requires a non-layered window for DirectComposition, VBlank frame pacing,
            // link clicks, and input. Strip WS_EX_LAYERED and ensure full opacity when browser is showing.
            if (!_settings.ClickThrough)
            {
                Opacity = 1.00;
                if (IsHandleCreated)
                {
                    NativeMethods.SetClickThrough(Handle, false, stripLayered: true);
                }
            }
            _ = _browserPanel.InitializeBrowserAsync();
        }
        else
        {
            if (!_settings.ClickThrough)
            {
                Opacity = Math.Clamp(_settings.Opacity, 0.10, 1.00);
            }
        }
    }

    private void DragWindow()
    {
        NativeMethods.ReleaseCapture();
        NativeMethods.SendMessage(Handle, NativeMethods.WM_NCLBUTTONDOWN, (IntPtr)NativeMethods.HT_CAPTION, IntPtr.Zero);

        _settings.X = Location.X;
        _settings.Y = Location.Y;
        _settings.Save();
    }

    private bool HasChatTarget => _settings.ChatTargetX.HasValue && _settings.ChatTargetY.HasValue &&
                                  !string.IsNullOrWhiteSpace(_settings.ChatTargetUrl);

    private void BeginChatTargetCapture()
    {
        if (!_settings.ShowBrowser)
        {
            UpdateScreenshotStatus("Open the browser, then choose the ChatGPT message composer.");
            return;
        }
        if (_capturingChatTarget)
        {
            CancelChatTargetCapture();
            return;
        }
        _capturingChatTarget = true;
        UpdateScreenshotStatus("Target capture: click the ChatGPT message composer (Esc cancels).");
    }

    private void OnActiveWebViewPointerDown(Point point)
    {
        if (!_capturingChatTarget) return;
        if (!ChatInputInjector.IsAiSiteUrl(_browserPanel.ActiveUrl))
        {
            CancelChatTargetCapture();
            UpdateScreenshotStatus("Open ChatGPT (or another supported chat) before setting a composer target.");
            return;
        }
        _capturingChatTarget = false;
        _settings.ChatTargetX = point.X;
        _settings.ChatTargetY = point.Y;
        _settings.ChatTargetUrl = _browserPanel.ActiveUrl;
        _settings.Save();
        UpdateScreenshotStatus("Composer target saved for this chat page.");
    }

    private void CancelChatTargetCapture()
    {
        if (!_capturingChatTarget) return;
        _capturingChatTarget = false;
        UpdateScreenshotStatus("Target capture cancelled.");
    }

    private void ClearChatTarget()
    {
        _capturingChatTarget = false;
        _settings.ChatTargetX = null;
        _settings.ChatTargetY = null;
        _settings.ChatTargetUrl = null;
        _settings.Save();
        UpdateScreenshotStatus("Target cleared.");
    }

    private void SetSidebarVisible(bool visible)
    {
        _sidebarVisible = visible;
        _screenshotSidebar.Visible = visible;
        _sidebarSplitter.Visible = visible;
        _btnConfigureChatTarget.Text = visible ? "◎" : "▸";
        _pnlContentHost.PerformLayout();
    }

    private void UpdateScreenshotStatus(string message)
    {
        _screenshotSidebar.SetStatus(message);
        _notifyIcon?.ShowBalloonTip(1400, "MyOverlay screenshots", message, ToolTipIcon.Info);
    }

    private async Task CaptureScreenshotAsync()
    {
        if (_settings.ClickThrough) { UpdateScreenshotStatus("Disable click-through before capturing a screenshot."); return; }
        try
        {
            UpdateScreenshotStatus("Capturing screen…");
            var bounds = Screen.FromControl(this).Bounds;
            var bitmap = await Task.Run(() =>
            {
                var shot = new Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                using var graphics = Graphics.FromImage(shot);
                graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
                return shot;
            });
            _screenshotSidebar.AddScreenshot(bitmap);
            if (!_sidebarVisible) SetSidebarVisible(true);

            await InsertScreenshotIntoComposerAsync(bitmap);
        }
        catch (Exception ex)
        {
            UpdateScreenshotStatus($"Screenshot capture failed: {ex.Message}");
        }
    }

    private async Task InsertScreenshotIntoComposerAsync(Image screenshot)
    {
        if (!HasChatTarget)
        {
            UpdateScreenshotStatus("Set a message-composer target before inserting a screenshot.");
            return;
        }
        if (!string.Equals(_settings.ChatTargetUrl, _browserPanel.ActiveUrl, StringComparison.Ordinal))
        {
            UpdateScreenshotStatus("The chat page changed since this target was saved. Set the target again.");
            return;
        }
        if (!ClipboardHelper.TrySetImage(screenshot, out var clipboardError))
        {
            UpdateScreenshotStatus($"Clipboard is unavailable: {clipboardError}");
            return;
        }

        // Native WinForms-to-WebView drag data is not an attachment format ChatGPT can rely on.
        // Use the supported Chromium clipboard path, focused through the saved CSS viewport point.
        SetOverlayActivationAllowed(true);
        Activate();
        int x = _settings.ChatTargetX!.Value;
        int y = _settings.ChatTargetY!.Value;
        string focusScript = $"(() => {{ const direct = document.querySelector('#prompt-textarea, div.ProseMirror[contenteditable=true], textarea[placeholder*=Message i], textarea[placeholder*=Ask i]'); const e = direct || document.elementFromPoint({x}, {y}); if (!e) return false; const t = e.closest('[contenteditable=true], textarea, input') || e; if (!t.matches?.('[contenteditable=true], textarea, input')) return false; t.focus?.(); t.click?.(); return document.activeElement === t || t.contains(document.activeElement); }})()";
        string? focused = await _browserPanel.ExecuteInActiveTabAsync(focusScript);
        if (string.IsNullOrWhiteSpace(focused) || focused == "false")
        {
            UpdateScreenshotStatus("The composer is unavailable. Set the target again.");
            return;
        }
        _browserPanel.FocusActiveWebView();
        await Task.Delay(80);
        SendKeys.SendWait("^v");
        UpdateScreenshotStatus("Screenshot pasted into the composer. Review it, then send when ready.");
    }

    private void SetOverlayActivationAllowed(bool allowed)
    {
        if (!IsHandleCreated) return;
        nint style = NativeMethods.GetWindowLongPtr(Handle, NativeMethods.GWL_EXSTYLE);
        style = allowed ? style & ~(nint)NativeMethods.WS_EX_NOACTIVATE : style | (nint)NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLongPtr(Handle, NativeMethods.GWL_EXSTYLE, style);
        NativeMethods.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
    }

    protected override void SetVisibleCore(bool value)
    {
        // Target-app configuration may intentionally defer a subsequent show, but a
        // normal Application.Run startup must always be allowed to display the overlay.
        if (value && !_allowOverlayVisible)
        {
            if (!IsHandleCreated)
                CreateHandle();

            base.SetVisibleCore(false);
            return;
        }

        base.SetVisibleCore(value);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        System.Threading.Interlocked.Exchange(ref _topmostWindowHandle, Handle.ToInt64());
        _hotkeyWindowHandle = Handle;

        if (!_initialTargetPromptShown && string.IsNullOrWhiteSpace(_targetExecutable))
        {
            _initialTargetPromptShown = true;
            BeginInvoke(new Action(() =>
            {
                if (!IsDisposed && string.IsNullOrWhiteSpace(_targetExecutable))
                    ConfigureTargetExecutable(showCancel: true);
            }));
        }

        if (_settings.AlwaysOnTop && Visible)
            EnforceTopMost();

        NativeMethods.SetClickThrough(Handle, _settings.ClickThrough, stripLayered: (_settings.ShowBrowser || _settings.Opacity >= 1.0));

        // Apply screen capture exclusion affinity (WDA_EXCLUDEFROMCAPTURE)
        ScreenCaptureProtection.Enabled = _settings.ScreenCaptureProtection;
        ScreenCaptureProtection.ApplyProtection(Handle);

        RegisterOverlayHotkeys();

        // Initialize browser if enabled on start
        if (_settings.ShowBrowser)
        {
            _ = _browserPanel.InitializeBrowserAsync();
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        // Do not let a worker callback act on a handle WinForms is destroying/recreating.
        System.Threading.Interlocked.Exchange(ref _topmostWindowHandle, 0);
        UnregisterOverlayHotkeys();
        base.OnHandleDestroyed(e);
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);

        _settings.X = Location.X;
        _settings.Y = Location.Y;
        _settings.Width = Width;
        _settings.Height = Height;
        _settings.Save();

        // Ensure display affinity persists across resizes
        ScreenCaptureProtection.ApplyProtection(Handle);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        // Accept WinForms' DPI-aware suggested bounds, then persist them so reopening
        // on the same monitor layout remains sensible.
        if (e.SuggestedRectangle.Width > 0 && e.SuggestedRectangle.Height > 0)
        {
            Bounds = e.SuggestedRectangle;
            _settings.X = Left;
            _settings.Y = Top;
            _settings.Width = Width;
            _settings.Height = Height;
            _settings.Save();
        }
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        // Once the user returns to another program, subsequent overlay-toolbar clicks should
        // not pull focus back. WebView interaction can opt in again through WM_MOUSEACTIVATE.
        SetOverlayActivationAllowed(false);
    }

    protected override void WndProc(ref Message m)
    {
        // Handle Global Hotkeys
        if (m.Msg == NativeMethods.WM_HOTKEY)
        {
            int id = m.WParam.ToInt32();
            switch (id)
            {
                case HOTKEY_CLICK_THROUGH:
                    ToggleClickThrough();
                    break;
                case HOTKEY_SETTINGS:
                    OpenSettings();
                    break;
                case HOTKEY_VISIBILITY:
                    ToggleVisibility();
                    break;
                case HOTKEY_TOGGLE_BROWSER:
                    ToggleBrowserPanel();
                    break;
                case HOTKEY_TRANSCRIPTION:
                    ToggleTranscription();
                    break;
                case HOTKEY_TOGGLE_TOPMOST:
                    ToggleAlwaysOnTop();
                    break;
            }
            return;
        }

        if (m.Msg == NativeMethods.WM_MOUSEACTIVATE && !_settings.ClickThrough)
        {
            if (NativeMethods.GetCursorPos(out var cursor) && _browserPanel.IsPointInActiveWebView(cursor))
            {
                // Typing/clicking inside Chromium is the explicit exception to no-activation.
                SetOverlayActivationAllowed(true);
                base.WndProc(ref m);
                return;
            }
            // Buttons and the screenshot shelf remain clickable but leave the other app's
            // keyboard focus alone.
            m.Result = (IntPtr)NativeMethods.MA_NOACTIVATE;
            return;
        }

        // Enable borderless window edge/corner resizing when not in click-through mode
        if (m.Msg == NativeMethods.WM_NCHITTEST && !_settings.ClickThrough)
        {
            base.WndProc(ref m);
            if (m.Result == (IntPtr)NativeMethods.HTCLIENT)
            {
                int x = unchecked((short)(m.LParam.ToInt64() & 0xFFFF));
                int y = unchecked((short)((m.LParam.ToInt64() >> 16) & 0xFFFF));
                Point clientPt = PointToClient(new Point(x, y));

                const int margin = 8;
                bool left = clientPt.X < margin;
                bool right = clientPt.X >= ClientSize.Width - margin;
                bool top = clientPt.Y < margin;
                bool bottom = clientPt.Y >= ClientSize.Height - margin;

                if (top && left) m.Result = (IntPtr)NativeMethods.HTTOPLEFT;
                else if (top && right) m.Result = (IntPtr)NativeMethods.HTTOPRIGHT;
                else if (bottom && left) m.Result = (IntPtr)NativeMethods.HTBOTTOMLEFT;
                else if (bottom && right) m.Result = (IntPtr)NativeMethods.HTBOTTOMRIGHT;
                else if (left) m.Result = (IntPtr)NativeMethods.HTLEFT;
                else if (right) m.Result = (IntPtr)NativeMethods.HTRIGHT;
                else if (top) m.Result = (IntPtr)NativeMethods.HTTOP;
                else if (bottom) m.Result = (IntPtr)NativeMethods.HTBOTTOM;
            }
            return;
        }

        base.WndProc(ref m);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        if (_settings.ShowBorder)
        {
            int thickness = Math.Max(1, _settings.BorderThickness);
            using var borderPen = new Pen(_settings.GetBorderColor(), thickness);
            e.Graphics.DrawRectangle(borderPen, thickness / 2, thickness / 2, Width - thickness, Height - thickness);
        }
    }

    public void ToggleClickThrough()
    {
        _settings.ClickThrough = !_settings.ClickThrough;
        bool strip = !_settings.ClickThrough && (_settings.ShowBrowser || _settings.Opacity >= 1.0);
        NativeMethods.SetClickThrough(Handle, _settings.ClickThrough, stripLayered: strip);

        if (!_settings.ClickThrough && _settings.ShowBrowser)
        {
            Opacity = 1.00;
        }

        _settings.Save();

        string msg = _settings.ClickThrough
            ? "Click-Through ENABLED. Clicks will pass through to background apps.\nPress Ctrl+Alt+C to disable."
            : "Click-Through DISABLED. Overlay can now be clicked and dragged.";

        _notifyIcon.ShowBalloonTip(1800, "MyOverlay", msg, ToolTipIcon.Info);
        Invalidate();
    }

    private void LoadTargetExecutable()
    {
        try
        {
            if (File.Exists(_targetConfigPath))
                _targetExecutable = File.ReadAllText(_targetConfigPath).Trim();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not load target executable setting: {ex.Message}");
            _targetExecutable = string.Empty;
        }
    }

    private void ConfigureTargetExecutable(bool showCancel)
    {
        using var dialog = new TargetExecutableDialog(_targetExecutable);
        // During first-run setup the overlay is intentionally hidden, so do not
        // assign the invisible overlay as the dialog owner.
        DialogResult dialogResult = Visible ? dialog.ShowDialog(this) : dialog.ShowDialog();
        if (dialogResult != DialogResult.OK)
            return;

        string value = dialog.TargetExecutable.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(value))
            return;

        // A full path must exist. A process filename (for example, App.exe) is also accepted.
        bool isPath = value.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || Path.IsPathRooted(value) || value.Contains(Path.DirectorySeparatorChar) || value.Contains(Path.AltDirectorySeparatorChar);
        if (isPath && !File.Exists(value))
        {
            const string message = "The executable path does not exist. Browse to the .exe file or enter a process filename such as App.exe.";
            if (Visible)
                MessageBox.Show(this, message, "Target application not found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else
                MessageBox.Show(message, "Target application not found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _targetExecutable = value;
        try
        {
            string? directory = Path.GetDirectoryName(_targetConfigPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(_targetConfigPath, _targetExecutable);
            _targetWindowWasReady = false;
            _allowOverlayVisible = false;
            if (Visible)
                Hide();
            _notifyIcon.ShowBalloonTip(1800, "MyOverlay target saved",
                $"Target: {Path.GetFileName(_targetExecutable)}\nMyOverlay will move to topmost when its main window is ready.",
                ToolTipIcon.Info);
            CheckTargetApplication();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save the target application setting.\n{ex.Message}",
                "MyOverlay", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void CheckTargetApplication()
    {
        if (IsDisposed || string.IsNullOrWhiteSpace(_targetExecutable))
            return;

        bool ready = FindTargetMainWindow(out _);
        if (!ready)
        {
            if (_targetWindowWasReady)
            {
                // Target closed: hide MyOverlay and reset the delay for the next launch.
                _targetWindowWasReady = false;
                _overlayActivationCompleted = false;
                _targetReadySinceUtc = DateTime.MinValue;
                _allowOverlayVisible = false;

                if (Visible)
                    Hide();

                TopMost = false;
            }
            return;
        }

        // Start the 20-second delay only once the target has a usable main window.
        if (!_targetWindowWasReady)
        {
            _targetWindowWasReady = true;
            _overlayActivationCompleted = false;
            _targetReadySinceUtc = DateTime.UtcNow;
            return;
        }

        // Keep the overlay hidden while the target finishes loading.
        if (_overlayActivationCompleted ||
            DateTime.UtcNow - _targetReadySinceUtc < TargetLaunchDelay)
        {
            return;
        }

        // Activate once after the 20-second delay. Ctrl+Alt+J can subsequently
        // turn topmost OFF without this monitor immediately turning it back on.
        _overlayActivationCompleted = true;
        _allowOverlayVisible = true;

        if (!Visible)
            Show();

        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;

        _settings.AlwaysOnTop = true;
        TopMost = true;
        _settings.Save();
        EnforceTopMost();

        if (_settings.ScreenCaptureProtection && IsHandleCreated)
            ScreenCaptureProtection.ApplyProtection(Handle);
    }

    private bool FindTargetMainWindow(out IntPtr targetWindow)
    {
        targetWindow = IntPtr.Zero;
        string configured = _targetExecutable.Trim().Trim('"');
        string configuredFile = Path.GetFileName(configured);
        string configuredName = Path.GetFileNameWithoutExtension(configuredFile);
        bool configuredAsPath = Path.IsPathRooted(configured) || configured.Contains(Path.DirectorySeparatorChar) || configured.Contains(Path.AltDirectorySeparatorChar);

        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.HasExited)
                        continue;

                    bool matches;
                    if (configuredAsPath)
                    {
                        string? processPath = process.MainModule?.FileName;
                        matches = !string.IsNullOrWhiteSpace(processPath) &&
                            string.Equals(Path.GetFullPath(processPath), Path.GetFullPath(configured), StringComparison.OrdinalIgnoreCase);
                    }
                    else
                    {
                        matches = string.Equals(process.ProcessName, configuredName, StringComparison.OrdinalIgnoreCase);
                    }

                    if (!matches)
                        continue;

                    // Wait for the actual top-level/main window; process creation alone is not enough.
                    IntPtr mainWindow = process.MainWindowHandle;
                    if (mainWindow != IntPtr.Zero && IsWindow(mainWindow))
                    {
                        targetWindow = mainWindow;
                        return true;
                    }
                }
                catch
                {
                    // Some processes deny access to MainModule. Continue searching others.
                }
            }
        }

        return false;
    }

    public void ToggleAlwaysOnTop()
    {
        _settings.AlwaysOnTop = !_settings.AlwaysOnTop;
        _keepTopmost = _settings.AlwaysOnTop;
        TopMost = _settings.AlwaysOnTop;
        if (_settings.AlwaysOnTop && Visible && IsHandleCreated)
            EnforceTopMost();
        _settings.Save();
    }

    private void EnforceTopMost()
    {
        if (!_settings.AlwaysOnTop || !IsHandleCreated || IsDisposed)
            return;

        // HWND_TOPMOST moves the window to the top of the topmost band.
        // NOACTIVATE prevents this maintenance from stealing keyboard focus.
        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, TOPMOST_FLAGS);
    }

    private void EnforceTopMostFromWorker()
    {
        // This callback runs on a worker thread. It reads only atomic lifecycle state
        // and calls Win32 APIs; it never accesses controls, WebView2, or Form properties.
        if (_isClosing || !_keepTopmost)
            return;

        IntPtr hwnd = new IntPtr(System.Threading.Interlocked.Read(ref _topmostWindowHandle));

        if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || !IsWindowVisible(hwnd))
            return;

        // Reassert roughly every 16 ms, similar to a per-frame render-loop check.
        // This is ordinary window Z-order management, not a guarantee against
        // exclusive-fullscreen, protected desktop, or OS-enforced restrictions.
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, TOPMOST_FLAGS);
    }

    public void CenterOnScreen()
    {
        Rectangle screen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Location = new Point(
            Math.Max(0, (screen.Width - Width) / 2),
            Math.Max(0, (screen.Height - Height) / 2)
        );
        _settings.X = Location.X;
        _settings.Y = Location.Y;
        _settings.Save();
    }

    public void ToggleVisibility()
    {
        if (Visible)
        {
            Hide();
            return;
        }

        // A user-requested restore always wins over target-app waiting state.
        _allowOverlayVisible = true;
        RestoreWindowBounds(_settings);
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        Show();
        EnforceTopMost();
        if (IsHandleCreated && _settings.ScreenCaptureProtection)
        {
            ScreenCaptureProtection.ApplyProtection(Handle);
        }
    }

    private void RegisterOverlayHotkeys()
    {
        RegisterHotkey(HOTKEY_CLICK_THROUGH, NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, Keys.C);
        RegisterHotkey(HOTKEY_SETTINGS, NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, Keys.S);
        RegisterHotkey(HOTKEY_VISIBILITY, NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, Keys.H);
        RegisterHotkey(HOTKEY_TOGGLE_BROWSER, NativeMethods.MOD_CONTROL | NativeMethods.MOD_NOREPEAT, Keys.B);
        RegisterHotkey(HOTKEY_TRANSCRIPTION, NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, Keys.T);
        RegisterHotkey(HOTKEY_TOGGLE_TOPMOST, NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, Keys.J);
    }

    private void RegisterHotkey(int id, uint modifiers, Keys key)
    {
        if (_registeredHotkeys.Contains(id)) return;
        if (_hotkeyWindowHandle != IntPtr.Zero && NativeMethods.RegisterHotKey(_hotkeyWindowHandle, id, modifiers, (uint)key))
            _registeredHotkeys.Add(id);
        else
            Debug.WriteLine($"[MyOverlay] Global hotkey registration failed: id={id}, key={key}.");
    }

    private void UnregisterOverlayHotkeys()
    {
        if (_hotkeyWindowHandle != IntPtr.Zero)
        {
            foreach (int id in _registeredHotkeys)
                NativeMethods.UnregisterHotKey(_hotkeyWindowHandle, id);
        }
        _registeredHotkeys.Clear();
        _hotkeyWindowHandle = IntPtr.Zero;
    }

    public void ToggleCaptureProtection()
    {
        _settings.ScreenCaptureProtection = !_settings.ScreenCaptureProtection;
        ScreenCaptureProtection.Enabled = _settings.ScreenCaptureProtection;
        if (IsHandleCreated)
        {
            ScreenCaptureProtection.ApplyProtection(Handle);
        }
        _settings.Save();

        string status = _settings.ScreenCaptureProtection ? "ENABLED" : "DISABLED";
        string detail = _settings.ScreenCaptureProtection
            ? "Overlay is completely invisible to Zoom, Teams, Meet, OBS, and Screenshots."
            : "Capture protection disabled. Window is now visible in screen recordings.";

        _notifyIcon.ShowBalloonTip(2000, "Screen Capture Protection", $"Anti-Capture Shield {status}\n{detail}", ToolTipIcon.Info);
        Invalidate();
    }

    public void ToggleSystemAudioCapture()
    {
        _settings.CaptureSystemAudio = !_settings.CaptureSystemAudio;
        _audioManager.ApplySettings(_settings);
        _settings.Save();

        string status = _settings.CaptureSystemAudio ? "ENABLED" : "DISABLED";
        string detail = _settings.CaptureSystemAudio
            ? $"Capturing computer audio from {_audioManager.SystemAudio.CurrentDeviceName ?? "Windows default output"} (WASAPI loopback)."
            : "System audio capture stopped.";

        _notifyIcon.ShowBalloonTip(2000, "System Audio Capture", $"System Audio {status}\n{detail}", ToolTipIcon.Info);
    }

    public void ToggleVirtualAudioRoute()
    {
        _settings.RouteToVirtualAudio = !_settings.RouteToVirtualAudio;

        // If enabled and no virtual device is selected yet, pick first detected virtual playback device
        if (_settings.RouteToVirtualAudio && string.IsNullOrWhiteSpace(_settings.VirtualAudioPlaybackDeviceId))
        {
            var virtuals = AudioDeviceDiagnostics.GetVirtualPlaybackDevices();
            if (virtuals.Count > 0)
            {
                _settings.VirtualAudioPlaybackDeviceId = virtuals[0].Id;
            }
        }

        _audioManager.ApplySettings(_settings);
        _settings.Save();

        string status = _settings.RouteToVirtualAudio ? "ENABLED" : "DISABLED";
        string detail = _settings.RouteToVirtualAudio
            ? $"Routing computer audio to: {_audioManager.VirtualBridge.TargetDeviceName ?? "Virtual Audio Device"}"
            : "Virtual audio routing stopped.";

        _notifyIcon.ShowBalloonTip(2000, "Virtual Audio Routing", $"Virtual Audio Route {status}\n{detail}", ToolTipIcon.Info);
    }

    public void OpenSettings()
    {
        if (_settingsForm != null && !_settingsForm.IsDisposed)
        {
            _settingsForm.BringToFront();
            _settingsForm.Activate();
            return;
        }

        _settingsForm = new SettingsForm(this, _settings);
        _settingsForm.Show();
    }

    public void OpenMicrophoneTestPage()
    {
        if (!_settings.ShowBrowser)
        {
            ToggleBrowserPanel();
        }
        _browserPanel.OpenMicTestTab();
    }

    public async void ToggleTranscription()
    {
        if (_transcriptionService.IsRunning)
        {
            await _transcriptionService.StopAsync();
        }
        else
        {
            bool started = await _transcriptionService.StartAsync();
            if (started && !_settings.ShowBrowser)
            {
                ToggleBrowserPanel();
            }
        }
    }

    private void OnTranscriptionNotification(object? sender, string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => OnTranscriptionNotification(sender, message)));
            return;
        }

        _notifyIcon.ShowBalloonTip(2000, "Background Transcription", message, ToolTipIcon.Info);
    }

    private void OnTranscriptionStateChanged(object? sender, bool isRunning)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => OnTranscriptionStateChanged(sender, isRunning)));
            return;
        }

        UpdateTranscriptionButton(isRunning);
    }

    private void UpdateTranscriptionButton(bool isRunning)
    {
        _btnTranscription.Text = isRunning ? "🔴 Transcribing" : "🎙️ Transcribe";
        _btnTranscription.BackColor = isRunning ? Color.FromArgb(185, 28, 28) : Color.FromArgb(32, 32, 40);
        _btnTranscription.ForeColor = isRunning ? Color.White : Color.Gainsboro;

        if (!isRunning)
        {
            _lblAudioStatus.Text = "⚪ Audio: Ready";
            _lblAudioStatus.ForeColor = Color.FromArgb(156, 163, 175);
            _lblAudioStatus.BackColor = Color.FromArgb(28, 28, 35);
        }
        else
        {
            _lblAudioStatus.Text = "🟡 Listening: 0%";
            _lblAudioStatus.ForeColor = Color.FromArgb(250, 204, 21);
            _lblAudioStatus.BackColor = Color.FromArgb(35, 32, 20);
        }
    }

    private void OnTranscriptionAudioLevelChanged(object? sender, float peak)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => OnTranscriptionAudioLevelChanged(sender, peak)));
            return;
        }

        if (_transcriptionService.IsRunning)
        {
            if (peak > 0.003f)
            {
                int pct = Math.Min(100, (int)(peak * 100));
                _lblAudioStatus.Text = $"🟢 Capturing {pct}%";
                _lblAudioStatus.ForeColor = Color.FromArgb(74, 222, 128);
                _lblAudioStatus.BackColor = Color.FromArgb(20, 38, 25);
            }
            else
            {
                _lblAudioStatus.Text = "🟡 Listening: 0%";
                _lblAudioStatus.ForeColor = Color.FromArgb(250, 204, 21);
                _lblAudioStatus.BackColor = Color.FromArgb(35, 32, 20);
            }
        }
    }

    private void HandleCustomShortcut(Keys keys)
    {
        if (keys == (Keys.Control | Keys.B))
        {
            ToggleBrowserPanel();
        }
        else if (keys == (Keys.Control | Keys.Alt | Keys.C))
        {
            ToggleClickThrough();
        }
        else if (keys == (Keys.Control | Keys.Alt | Keys.S))
        {
            OpenSettings();
        }
        else if (keys == (Keys.Control | Keys.Alt | Keys.H))
        {
            ToggleVisibility();
        }
        else if (keys == (Keys.Control | Keys.Alt | Keys.T))
        {
            ToggleTranscription();
        }
    }

    private void ExitApplication()
    {
        Close();
        Application.Exit();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _isClosing = true;
        System.Threading.Interlocked.Exchange(ref _topmostWindowHandle, 0);
        var topmostEnforcer = System.Threading.Interlocked.Exchange(ref _topmostEnforcer, null);
        if (topmostEnforcer != null)
        {
            topmostEnforcer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            topmostEnforcer.Dispose();
        }
        _targetProcessMonitor.Stop();
        _targetProcessMonitor.Dispose();

        UnregisterOverlayHotkeys();

        _transcriptionService.Dispose();
        _audioManager.Dispose();

        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }

        _appIcon?.Dispose();
        ScreenCaptureProtection.Shutdown();

        base.OnFormClosing(e);
    }

    private Button CreateHeaderButton(string text, string toolTip, EventHandler onClick)
    {
        var btn = new Button
        {
            Text = text,
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = Color.Gainsboro,
            BackColor = Color.FromArgb(32, 32, 40),
            FlatStyle = FlatStyle.Flat,
            Size = new Size(24, 22),
            Cursor = Cursors.Hand
        };
        btn.FlatAppearance.BorderSize = 0;
        btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 60, 75);
        btn.Click += onClick;

        var tip = new ToolTip();
        tip.SetToolTip(btn, toolTip);

        return btn;
    }

    private static Icon CreateAppIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(Color.FromArgb(99, 102, 241));
            g.FillEllipse(brush, 1, 1, 30, 30);

            using var textBrush = new SolidBrush(Color.White);
            using var font = new Font("Segoe UI", 13f, FontStyle.Bold);
            using var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            g.DrawString("O", font, textBrush, new RectangleF(0, 0, 32, 32), sf);
        }

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using var tempIcon = Icon.FromHandle(hIcon);
            return (Icon)tempIcon.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(hIcon);
        }
    }

    private sealed class TargetExecutableDialog : Form
    {
        private readonly TextBox _pathBox;
        public string TargetExecutable => _pathBox.Text;

        public TargetExecutableDialog(string currentValue)
        {
            Text = "MyOverlay — Target application";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = false;
            ClientSize = new Size(560, 155);
            Font = new Font("Segoe UI", 9F);

            var label = new Label
            {
                Text = "Target executable path or process filename (.exe):",
                AutoSize = true,
                Location = new Point(14, 14)
            };
            _pathBox = new TextBox
            {
                Text = currentValue,
                Location = new Point(14, 40),
                Width = 430,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            var browse = new Button
            {
                Text = "Browse...",
                Location = new Point(452, 38),
                Width = 94,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            browse.Click += (_, _) =>
            {
                using var picker = new OpenFileDialog
                {
                    Title = "Select target application",
                    Filter = "Applications (*.exe)|*.exe|All files (*.*)|*.*",
                    CheckFileExists = true,
                    Multiselect = false,
                    FileName = _pathBox.Text.Contains(Path.DirectorySeparatorChar) ? _pathBox.Text : string.Empty
                };
                if (picker.ShowDialog(this) == DialogResult.OK)
                    _pathBox.Text = picker.FileName;
            };

            var hint = new Label
            {
                Text = "When the target's main window is ready, MyOverlay will show and enable Always On Top.\nPress Ctrl+Alt+J to toggle topmost ON/OFF at any time.",
                AutoSize = true,
                Location = new Point(14, 73),
                ForeColor = Color.DimGray
            };
            var save = new Button
            {
                Text = "Save",
                DialogResult = DialogResult.OK,
                Location = new Point(370, 116),
                Width = 82
            };
            var cancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(464, 116),
                Width = 82
            };

            Controls.AddRange(new Control[] { label, _pathBox, browse, hint, save, cancel });
            AcceptButton = save;
            CancelButton = cancel;
        }
    }

}
