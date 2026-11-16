using System;
using System.Drawing;
using NAudio.CoreAudioApi;

namespace MyOverlay;

public class SettingsForm : Form
{
    private readonly OverlayForm _overlay;
    private readonly AppSettings _settings;

    // Controls - Appearance & Text
    private TextBox _txtOverlayText = null!;
    private Label _lblFontPreview = null!;
    private Panel _pnlTextColor = null!;
    private Panel _pnlBgColor = null!;
    private Panel _pnlBorderColor = null!;
    private TrackBar _trkOpacity = null!;
    private Label _lblOpacityValue = null!;

    // Controls - Position & Size
    private NumericUpDown _numX = null!;
    private NumericUpDown _numY = null!;
    private NumericUpDown _numWidth = null!;
    private NumericUpDown _numHeight = null!;

    // Controls - Browser Settings
    private TextBox _txtHomePage = null!;
    private TextBox _txtSearchTemplate = null!;
    private CheckBox _chkShowBrowserStartup = null!;

    // Controls - System Audio Capture (WASAPI Loopback)
    private CheckBox _chkCaptureSystemAudio = null!;
    private ComboBox _cboPlaybackDevice = null!;
    private Button _btnRefreshAudioDevices = null!;
    private Label _lblSystemAudioStatus = null!;
    private ProgressBar _prgSystemAudioLevel = null!;

    // Controls - Virtual Audio Routing to WebView2 Browser
    private CheckBox _chkRouteToVirtualAudio = null!;
    private ComboBox _cboVirtualPlaybackDevice = null!;
    private Label _lblVirtualDriverInfo = null!;
    private Label _lblPairedCaptureInfo = null!;
    private Label _lblBridgeStatus = null!;
    private ProgressBar _prgBridgeLevel = null!;
    private Label _lblFeedbackWarning = null!;
    private CheckBox _chkAutoSwitchDefaultMic = null!;
    private Button _btnSetDefaultMic = null!;
    private Button _btnRestoreDefaultMic = null!;
    private Button _btnOpenSoundControlPanel = null!;
    private Button _btnOpenMicTestPage = null!;

    // Controls - WebView2 Microphone Permissions & Diagnostics
    private CheckBox _chkAutoGrantMic = null!;
    private Label _lblPermissionStatus = null!;
    private Button _btnAudioDiagnostics = null!;

    // Controls - Microphone Pipeline
    private ComboBox _cboAudioSource = null!;
    private ComboBox _cboMicrophoneDevice = null!;
    private Label _lblMicrophoneStatus = null!;
    private ProgressBar _prgMicrophoneLevel = null!;

    // VoiceMeeter Routing Matrix Controls
    private CheckBox _chkRouteSystemToVirtual = null!;
    private CheckBox _chkRouteSystemToPhysical = null!;
    private CheckBox _chkRouteMicToVirtual = null!;
    private CheckBox _chkRouteMicToPhysical = null!;

    // Physical Playback Destination Controls
    private ComboBox _cboPhysicalOutputDevice = null!;
    private Label _lblPhysicalOutputStatus = null!;
    private ProgressBar _prgPhysicalOutputLevel = null!;
    private Label _lblPhysicalFeedbackWarning = null!;

    // Volume & Mute Controls
    private TrackBar _trkSystemVolume = null!;
    private Label _lblSystemVolume = null!;
    private Button _btnSystemMute = null!;

    private TrackBar _trkMicVolume = null!;
    private Label _lblMicVolume = null!;
    private Button _btnMicMute = null!;

    private TrackBar _trkVirtualVolume = null!;
    private Label _lblVirtualVolume = null!;
    private Button _btnVirtualMute = null!;

    private TrackBar _trkPhysicalVolume = null!;
    private Label _lblPhysicalVolume = null!;
    private Button _btnPhysicalMute = null!;

    // Controls - Behaviors
    private CheckBox _chkAlwaysOnTop = null!;
    private CheckBox _chkClickThrough = null!;
    private CheckBox _chkShowBorder = null!;
    private CheckBox _chkScreenCaptureProtection = null!;

    // Controls - Continuous Audio-to-Text (Transcription)
    private ComboBox _cboSpeechEngine = null!;
    private TextBox _txtSpeechApiKey = null!;
    private Button _btnToggleApiKeyVisibility = null!;
    private TextBox _txtSpeechEndpoint = null!;
    private TextBox _txtSpeechModel = null!;
    private TextBox _txtSpeechLanguage = null!;
    private NumericUpDown _numSilenceThreshold = null!;
    private NumericUpDown _numMaxChunkSeconds = null!;
    private Button _btnTestSpeechEngine = null!;
    private Label _lblSpeechStatus = null!;

    // Working copies
    private Font _selectedFont;
    private Color _selectedTextColor;
    private Color _selectedBgColor;
    private Color _selectedBorderColor;

    public SettingsForm(OverlayForm overlay, AppSettings settings, int initialTabIndex = 0)
    {
        _overlay = overlay;
        _settings = settings;

        _selectedFont = (Font)settings.Font.Clone();
        _selectedTextColor = settings.GetTextColor();
        _selectedBgColor = settings.GetBackgroundColor();
        _selectedBorderColor = settings.GetBorderColor();

        InitializeComponents(initialTabIndex);
        LoadValuesFromSettings();
        ScreenCaptureProtection.ProtectForm(this);

        // Hook real-time audio pipeline events for live level meters and status updates
        _overlay.AudioManager.SystemAudioLevelChanged += OnSystemAudioLevelChanged;
        _overlay.AudioManager.SystemAudio.StatusChanged += OnSystemAudioStatusChanged;
        _overlay.AudioManager.MicrophoneLevelChanged += OnMicrophoneLevelChanged;
        _overlay.AudioManager.Microphone.StatusChanged += OnMicrophoneStatusChanged;
        _overlay.AudioManager.SystemAudio.DeviceChanged += OnAudioDevicesChanged;

        // Hook Virtual & Physical Audio Bridge & WebView2 permission tracker events
        _overlay.AudioManager.VirtualBridge.BridgeLevelChanged += OnBridgeLevelChanged;
        _overlay.AudioManager.VirtualBridge.StatusChanged += OnBridgeStatusChanged;
        _overlay.AudioManager.PhysicalBridge.BridgeLevelChanged += OnPhysicalLevelChanged;
        _overlay.AudioManager.PhysicalBridge.StatusChanged += OnPhysicalStatusChanged;
        WebView2AudioPermissionTracker.PermissionEventLogged += OnPermissionEventLogged;
    }

    private void InitializeComponents(int initialTabIndex = 0)
    {
        Text = "MyOverlay - Settings";

        // Generous default size with auto-fit to screen working area
        Rectangle workingArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        int defaultWidth = Math.Min(960, Math.Max(880, workingArea.Width - 100));
        int defaultHeight = Math.Min(940, Math.Max(720, workingArea.Height - 80));
        Size = new Size(defaultWidth, defaultHeight);
        MinimumSize = new Size(760, 560);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        TopMost = true;

        var tabControl = new TabControl
        {
            Dock = DockStyle.Fill,
            Padding = new Point(14, 6)
        };

        // --- TAB 1: Browser & Navigation ---
        var tabBrowser = new TabPage("Browser");
        BuildBrowserTab(tabBrowser);
        tabControl.TabPages.Add(tabBrowser);

        // --- TAB 2: Notes & Appearance ---
        var tabAppearance = new TabPage("Notes & Appearance");
        BuildAppearanceTab(tabAppearance);
        tabControl.TabPages.Add(tabAppearance);

        // --- TAB 3: Position & Behaviors ---
        var tabBehavior = new TabPage("Position & Behavior");
        BuildBehaviorTab(tabBehavior);
        tabControl.TabPages.Add(tabBehavior);

        // --- TAB 4: Audio & Sound ---
        var tabAudio = new TabPage("Audio & Sound (VoiceMeeter Router)");
        BuildAudioTab(tabAudio);
        tabControl.TabPages.Add(tabAudio);

        // --- TAB 5: Continuous Audio-to-Text ---
        var tabTranscription = new TabPage("Audio-to-Text");
        BuildTranscriptionTab(tabTranscription);
        tabControl.TabPages.Add(tabTranscription);

        if (initialTabIndex > 0 && initialTabIndex < tabControl.TabPages.Count)
        {
            tabControl.SelectedIndex = initialTabIndex;
        }

        // Bottom Buttons Panel
        var bottomPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 62,
            Padding = new Padding(12)
        };

        var btnApply = new Button
        {
            Text = "Apply",
            Width = 90,
            Height = 32
        };
        btnApply.Click += (s, e) => ApplySettings(false);

        var btnSaveClose = new Button
        {
            Text = "Save & Close",
            Width = 105,
            Height = 32
        };
        btnSaveClose.Click += (s, e) =>
        {
            ApplySettings(true);
            Close();
        };

        var btnCancel = new Button
        {
            Text = "Cancel",
            Width = 85,
            Height = 32
        };
        btnCancel.Click += (s, e) => Close();

        bottomPanel.Controls.Add(btnApply);
        bottomPanel.Controls.Add(btnSaveClose);
        bottomPanel.Controls.Add(btnCancel);

        void RepositionBottomButtons()
        {
            int right = bottomPanel.ClientSize.Width - 15;
            btnCancel.Location = new Point(right - btnCancel.Width, 15);
            btnSaveClose.Location = new Point(btnCancel.Left - 10 - btnSaveClose.Width, 15);
            btnApply.Location = new Point(btnSaveClose.Left - 10 - btnApply.Width, 15);
        }
        bottomPanel.Resize += (s, e) => RepositionBottomButtons();

        Controls.Add(tabControl);
        Controls.Add(bottomPanel);
        bottomPanel.BringToFront();
        RepositionBottomButtons();
    }

    private void BuildBrowserTab(TabPage tab)
    {
        tab.Padding = new Padding(12);
        tab.AutoScroll = true;

        var grpGeneral = new GroupBox
        {
            Text = "Embedded Chromium Browser (WebView2)",
            Location = new Point(12, 12),
            Size = new Size(800, 235)
        };

        var lblHome = new Label
        {
            Text = "Default Home Page URL:",
            Location = new Point(15, 28),
            AutoSize = true
        };
        _txtHomePage = new TextBox
        {
            Location = new Point(15, 50),
            Width = 760,
            Font = new Font("Segoe UI", 9.5f)
        };

        var lblSearch = new Label
        {
            Text = "Search Engine Template ({0} is replaced by query):",
            Location = new Point(15, 88),
            AutoSize = true
        };
        _txtSearchTemplate = new TextBox
        {
            Location = new Point(15, 110),
            Width = 760,
            Font = new Font("Segoe UI", 9.5f)
        };

        var lblSearchExamples = new Label
        {
            Text = "Examples:\r\n• Google: https://www.google.com/search?q={0}\r\n• DuckDuckGo: https://duckduckgo.com/?q={0}\r\n• Bing: https://www.bing.com/search?q={0}",
            Location = new Point(15, 140),
            Size = new Size(760, 50),
            ForeColor = Color.DimGray,
            Font = new Font("Segoe UI", 8.5f)
        };

        _chkShowBrowserStartup = new CheckBox
        {
            Text = "Open Browser panel automatically on startup",
            Location = new Point(15, 198),
            AutoSize = true
        };

        grpGeneral.Controls.Add(lblHome);
        grpGeneral.Controls.Add(_txtHomePage);
        grpGeneral.Controls.Add(lblSearch);
        grpGeneral.Controls.Add(_txtSearchTemplate);
        grpGeneral.Controls.Add(lblSearchExamples);
        grpGeneral.Controls.Add(_chkShowBrowserStartup);
        tab.Controls.Add(grpGeneral);

        var grpShortcuts = new GroupBox
        {
            Text = "Browser Keyboard Shortcuts",
            Location = new Point(12, 255),
            Size = new Size(800, 165)
        };

        var lblBrowserShortcuts = new Label
        {
            Text = "• Ctrl + B : Toggle Browser / Notes view\r\n" +
                   "• Ctrl + T : Open New Tab\r\n" +
                   "• Ctrl + W : Close Current Tab\r\n" +
                   "• Ctrl + L : Focus Address Bar\r\n" +
                   "• Ctrl + R / F5 : Reload current page\r\n" +
                   "• Ctrl + Tab / Ctrl + Shift + Tab : Switch between tabs\r\n" +
                   "• Ctrl + F : Find text on page\r\n" +
                   "• F12 : Open Chromium Developer Tools",
            Location = new Point(15, 22),
            Size = new Size(760, 135),
            Font = new Font("Segoe UI", 9f)
        };
        grpShortcuts.Controls.Add(lblBrowserShortcuts);
        tab.Controls.Add(grpShortcuts);

        void UpdateBrowserLayout()
        {
            int contentW = Math.Max(700, tab.ClientSize.Width - 28);
            grpGeneral.Width = contentW;
            _txtHomePage.Width = Math.Max(200, contentW - 30);
            _txtSearchTemplate.Width = Math.Max(200, contentW - 30);
            lblSearchExamples.Width = Math.Max(200, contentW - 30);
            grpShortcuts.Width = contentW;
            lblBrowserShortcuts.Width = Math.Max(200, contentW - 30);
        }
        tab.Resize += (s, e) => UpdateBrowserLayout();
        UpdateBrowserLayout();
    }

    private void BuildAppearanceTab(TabPage tab)
    {
        tab.Padding = new Padding(12);
        tab.AutoScroll = true;

        // 1. Text Content Group
        var grpText = new GroupBox
        {
            Text = "Notes Overlay Text",
            Location = new Point(12, 10),
            Size = new Size(800, 175)
        };

        _txtOverlayText = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Location = new Point(12, 22),
            Size = new Size(770, 140),
            Font = new Font("Segoe UI", 9.5f)
        };
        grpText.Controls.Add(_txtOverlayText);
        tab.Controls.Add(grpText);

        // 2. Font & Colors Group
        var grpColors = new GroupBox
        {
            Text = "Typography & Colors",
            Location = new Point(12, 195),
            Size = new Size(800, 105)
        };

        var btnChooseFont = new Button
        {
            Text = "Choose Font...",
            Location = new Point(12, 24),
            Size = new Size(120, 28)
        };
        _lblFontPreview = new Label
        {
            Location = new Point(140, 28),
            Size = new Size(640, 20),
            Text = "Segoe UI, 12pt",
            AutoEllipsis = true
        };
        btnChooseFont.Click += (s, e) =>
        {
            using var fd = new FontDialog { Font = _selectedFont };
            if (fd.ShowDialog(this) == DialogResult.OK)
            {
                _selectedFont = fd.Font;
                UpdateFontPreviewLabel();
            }
        };
        grpColors.Controls.Add(btnChooseFont);
        grpColors.Controls.Add(_lblFontPreview);

        var btnTextColor = new Button
        {
            Text = "Text Color...",
            Location = new Point(12, 62),
            Size = new Size(120, 28)
        };
        _pnlTextColor = new Panel
        {
            Location = new Point(140, 64),
            Size = new Size(24, 24),
            BorderStyle = BorderStyle.FixedSingle
        };
        btnTextColor.Click += (s, e) =>
        {
            using var cd = new ColorDialog { Color = _selectedTextColor };
            if (cd.ShowDialog(this) == DialogResult.OK)
            {
                _selectedTextColor = cd.Color;
                _pnlTextColor.BackColor = _selectedTextColor;
            }
        };
        grpColors.Controls.Add(btnTextColor);
        grpColors.Controls.Add(_pnlTextColor);

        var btnBgColor = new Button
        {
            Text = "Background...",
            Location = new Point(200, 62),
            Size = new Size(120, 28)
        };
        _pnlBgColor = new Panel
        {
            Location = new Point(328, 64),
            Size = new Size(24, 24),
            BorderStyle = BorderStyle.FixedSingle
        };
        btnBgColor.Click += (s, e) =>
        {
            using var cd = new ColorDialog { Color = _selectedBgColor };
            if (cd.ShowDialog(this) == DialogResult.OK)
            {
                _selectedBgColor = cd.Color;
                _pnlBgColor.BackColor = _selectedBgColor;
            }
        };
        grpColors.Controls.Add(btnBgColor);
        grpColors.Controls.Add(_pnlBgColor);

        var btnBorderColor = new Button
        {
            Text = "Border Color...",
            Location = new Point(385, 62),
            Size = new Size(120, 28)
        };
        _pnlBorderColor = new Panel
        {
            Location = new Point(513, 64),
            Size = new Size(24, 24),
            BorderStyle = BorderStyle.FixedSingle
        };
        btnBorderColor.Click += (s, e) =>
        {
            using var cd = new ColorDialog { Color = _selectedBorderColor };
            if (cd.ShowDialog(this) == DialogResult.OK)
            {
                _selectedBorderColor = cd.Color;
                _pnlBorderColor.BackColor = _selectedBorderColor;
            }
        };
        grpColors.Controls.Add(btnBorderColor);
        grpColors.Controls.Add(_pnlBorderColor);

        tab.Controls.Add(grpColors);

        // 3. Opacity Group
        var grpOpacity = new GroupBox
        {
            Text = "Window Opacity",
            Location = new Point(12, 310),
            Size = new Size(800, 75)
        };

        _trkOpacity = new TrackBar
        {
            Minimum = 10,
            Maximum = 100,
            TickFrequency = 10,
            Location = new Point(12, 22),
            Size = new Size(690, 45)
        };
        _lblOpacityValue = new Label
        {
            Text = "95%",
            Location = new Point(715, 26),
            Size = new Size(60, 20),
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };
        _trkOpacity.ValueChanged += (s, e) =>
        {
            _lblOpacityValue.Text = $"{_trkOpacity.Value}%";
            _overlay.Opacity = _trkOpacity.Value / 100.0;
        };

        grpOpacity.Controls.Add(_trkOpacity);
        grpOpacity.Controls.Add(_lblOpacityValue);
        tab.Controls.Add(grpOpacity);

        void UpdateAppearanceLayout()
        {
            int contentW = Math.Max(700, tab.ClientSize.Width - 28);
            grpText.Width = contentW;
            _txtOverlayText.Width = Math.Max(200, contentW - 24);
            grpColors.Width = contentW;
            _lblFontPreview.Width = Math.Max(200, contentW - 160);
            grpOpacity.Width = contentW;
            _trkOpacity.Width = Math.Max(200, contentW - 100);
            _lblOpacityValue.Location = new Point(contentW - 80, 26);
        }
        tab.Resize += (s, e) => UpdateAppearanceLayout();
        UpdateAppearanceLayout();
    }

    private void BuildBehaviorTab(TabPage tab)
    {
        tab.Padding = new Padding(12);
        tab.AutoScroll = true;

        // 1. Position & Size Group
        var grpPos = new GroupBox
        {
            Text = "Position & Dimensions (Pixels)",
            Location = new Point(12, 10),
            Size = new Size(800, 110)
        };

        var lblX = new Label { Text = "X:", Location = new Point(15, 26), AutoSize = true };
        _numX = new NumericUpDown { Location = new Point(35, 24), Width = 75, Minimum = -5000, Maximum = 10000 };

        var lblY = new Label { Text = "Y:", Location = new Point(125, 26), AutoSize = true };
        _numY = new NumericUpDown { Location = new Point(145, 24), Width = 75, Minimum = -5000, Maximum = 10000 };

        var lblW = new Label { Text = "Width:", Location = new Point(235, 26), AutoSize = true };
        _numWidth = new NumericUpDown { Location = new Point(290, 24), Width = 80, Minimum = 200, Maximum = 10000 };

        var lblH = new Label { Text = "Height:", Location = new Point(385, 26), AutoSize = true };
        _numHeight = new NumericUpDown { Location = new Point(445, 24), Width = 80, Minimum = 150, Maximum = 10000 };

        var btnCenter = new Button
        {
            Text = "Center on Screen",
            Location = new Point(15, 64),
            Size = new Size(150, 30)
        };
        btnCenter.Click += (s, e) =>
        {
            Rectangle screen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
            int w = (int)_numWidth.Value;
            int h = (int)_numHeight.Value;
            _numX.Value = Math.Max(0, (screen.Width - w) / 2);
            _numY.Value = Math.Max(0, (screen.Height - h) / 2);
        };

        var btnSyncCurrent = new Button
        {
            Text = "Read Current Bounds",
            Location = new Point(180, 64),
            Size = new Size(160, 30)
        };
        btnSyncCurrent.Click += (s, e) =>
        {
            _numX.Value = _overlay.Location.X;
            _numY.Value = _overlay.Location.Y;
            _numWidth.Value = _overlay.Width;
            _numHeight.Value = _overlay.Height;
        };

        grpPos.Controls.Add(lblX);
        grpPos.Controls.Add(_numX);
        grpPos.Controls.Add(lblY);
        grpPos.Controls.Add(_numY);
        grpPos.Controls.Add(lblW);
        grpPos.Controls.Add(_numWidth);
        grpPos.Controls.Add(lblH);
        grpPos.Controls.Add(_numHeight);
        grpPos.Controls.Add(btnCenter);
        grpPos.Controls.Add(btnSyncCurrent);
        tab.Controls.Add(grpPos);

        // 2. Behavior Group
        var grpSec = new GroupBox
        {
            Text = "Window Behavior & Privacy",
            Location = new Point(12, 130),
            Size = new Size(800, 155)
        };

        _chkAlwaysOnTop = new CheckBox
        {
            Text = "Always On Top (Overlay stays above other windows)",
            Location = new Point(15, 24),
            AutoSize = true
        };

        _chkShowBorder = new CheckBox
        {
            Text = "Show Window Border (Accent border for easy sizing/viewing)",
            Location = new Point(15, 52),
            AutoSize = true
        };

        _chkClickThrough = new CheckBox
        {
            Text = "Click-Through Mode (Mouse clicks pass directly through to apps below)",
            Location = new Point(15, 80),
            AutoSize = true
        };

        _chkScreenCaptureProtection = new CheckBox
        {
            Text = "Anti-Capture Shield (Invisible to Zoom, Teams, Meet, OBS, Snipping Tool)",
            Location = new Point(15, 108),
            AutoSize = true
        };

        grpSec.Controls.Add(_chkAlwaysOnTop);
        grpSec.Controls.Add(_chkShowBorder);
        grpSec.Controls.Add(_chkClickThrough);
        grpSec.Controls.Add(_chkScreenCaptureProtection);
        tab.Controls.Add(grpSec);

        // 3. Hotkeys Info Group
        var grpHotkeys = new GroupBox
        {
            Text = "Overlay Global Hotkeys",
            Location = new Point(12, 295),
            Size = new Size(800, 140)
        };

        var lblHotkeys = new Label
        {
            Text = "• Ctrl + B : Toggle Browser Panel on / off\r\n" +
                   "• Ctrl + Alt + C : Toggle Click-Through mode\r\n" +
                   "• Ctrl + Alt + S : Open this Settings dialog\r\n" +
                   "• Ctrl + Alt + H : Hide / Show Overlay window\r\n" +
                   "• System Tray icon is always available in notification area.",
            Location = new Point(15, 22),
            Size = new Size(760, 105),
            Font = new Font("Segoe UI", 9f)
        };
        grpHotkeys.Controls.Add(lblHotkeys);
        tab.Controls.Add(grpHotkeys);

        void UpdateBehaviorLayout()
        {
            int contentW = Math.Max(700, tab.ClientSize.Width - 28);
            grpPos.Width = contentW;
            grpSec.Width = contentW;
            grpHotkeys.Width = contentW;
            lblHotkeys.Width = Math.Max(200, contentW - 30);
        }
        tab.Resize += (s, e) => UpdateBehaviorLayout();
        UpdateBehaviorLayout();
    }

    private void BuildAudioTab(TabPage tab)
    {
        tab.Padding = new Padding(12);
        tab.AutoScroll = true;

        int currentY = 10;

        // Banner
        var lblBanner = new Label
        {
            Text = "🎛️ VoiceMeeter Banana Matrix Router & Audio Mixer\r\nRoute System Audio & Microphone independently to Virtual Mic (Browser/AI) and Physical Output.",
            Location = new Point(12, currentY),
            Size = new Size(800, 38),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            ForeColor = Color.FromArgb(79, 70, 229)
        };
        tab.Controls.Add(lblBanner);
        currentY += 46;

        // -------------------------------------------------------------
        // STRIP 1: System / Computer Audio Input (WASAPI Loopback)
        // -------------------------------------------------------------
        var grpSystem = new GroupBox
        {
            Text = "INPUT 1: Computer / System Audio (WASAPI Loopback)",
            Location = new Point(12, currentY),
            Size = new Size(800, 228)
        };

        _chkCaptureSystemAudio = new CheckBox
        {
            Text = "Enable System Audio Capture",
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Location = new Point(15, 22),
            AutoSize = true
        };

        var lblPlaybackDev = new Label
        {
            Text = "Source Playback Device:",
            Location = new Point(15, 48),
            AutoSize = true
        };

        _cboPlaybackDevice = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(15, 68),
            Width = 650,
            Font = new Font("Segoe UI", 9f)
        };
        _cboPlaybackDevice.SelectedIndexChanged += (s, e) => UpdatePairedCaptureAndFeedbackDisplay();

        _btnRefreshAudioDevices = new Button
        {
            Text = "🔄 Refresh",
            Location = new Point(675, 67),
            Width = 110,
            Height = 28
        };
        _btnRefreshAudioDevices.Click += (s, e) => PopulateAudioDeviceDropdowns();

        var lblSysMeter = new Label { Text = "Live Level:", Location = new Point(15, 106), AutoSize = true };
        _prgSystemAudioLevel = new ProgressBar
        {
            Location = new Point(95, 106),
            Width = 290,
            Height = 16,
            Minimum = 0,
            Maximum = 100,
            Style = ProgressBarStyle.Continuous
        };

        _lblSystemAudioStatus = new Label
        {
            Text = "Status: Idle",
            Location = new Point(400, 106),
            Size = new Size(380, 18),
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = Color.DimGray
        };

        var lblSysVol = new Label { Text = "Gain / Vol:", Location = new Point(15, 136), AutoSize = true };
        _trkSystemVolume = new TrackBar
        {
            Location = new Point(95, 130),
            Width = 290,
            Height = 30,
            Minimum = 0,
            Maximum = 200,
            Value = 100,
            TickFrequency = 25,
            SmallChange = 5,
            LargeChange = 20
        };
        _lblSystemVolume = new Label { Text = "100%", Location = new Point(400, 136), AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
        _trkSystemVolume.Scroll += (s, e) =>
        {
            _lblSystemVolume.Text = $"{_trkSystemVolume.Value}%";
            _overlay.AudioManager.SystemAudioVolume = _trkSystemVolume.Value / 100.0f;
        };

        _btnSystemMute = new Button
        {
            Text = "🔊 ON",
            Location = new Point(460, 132),
            Size = new Size(85, 28),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
        };
        _btnSystemMute.Click += (s, e) =>
        {
            bool muted = !((bool)(_btnSystemMute.Tag ?? false));
            UpdateMuteButtonVisual(_btnSystemMute, muted);
            _overlay.AudioManager.SystemAudioMuted = muted;
        };

        var lblSysRoute = new Label
        {
            Text = "Routing Matrix:",
            Location = new Point(15, 174),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            AutoSize = true
        };

        _chkRouteSystemToVirtual = new CheckBox
        {
            Text = "🔀 Route to Virtual Mic (Browser / ChatGPT)",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(79, 70, 229),
            Location = new Point(145, 172),
            AutoSize = true
        };
        _chkRouteSystemToVirtual.CheckedChanged += (s, e) =>
        {
            _overlay.AudioManager.RouteSystemToVirtual = _chkRouteSystemToVirtual.Checked;
            UpdatePairedCaptureAndFeedbackDisplay();
        };

        _chkRouteSystemToPhysical = new CheckBox
        {
            Text = "🎧 Route to Headphones (Physical Output)",
            Font = new Font("Segoe UI", 8.5f),
            Location = new Point(145, 196),
            AutoSize = true
        };
        _chkRouteSystemToPhysical.CheckedChanged += (s, e) =>
        {
            _overlay.AudioManager.RouteSystemToPhysical = _chkRouteSystemToPhysical.Checked;
            UpdatePairedCaptureAndFeedbackDisplay();
        };

        _chkCaptureSystemAudio.CheckedChanged += (s, e) =>
        {
            bool enabled = _chkCaptureSystemAudio.Checked;
            _cboPlaybackDevice.Enabled = enabled;
            if (!enabled)
            {
                _prgSystemAudioLevel.Value = 0;
                _lblSystemAudioStatus.Text = "Status: Disabled";
            }
        };

        grpSystem.Controls.Add(_chkCaptureSystemAudio);
        grpSystem.Controls.Add(lblPlaybackDev);
        grpSystem.Controls.Add(_cboPlaybackDevice);
        grpSystem.Controls.Add(_btnRefreshAudioDevices);
        grpSystem.Controls.Add(lblSysMeter);
        grpSystem.Controls.Add(_prgSystemAudioLevel);
        grpSystem.Controls.Add(_lblSystemAudioStatus);
        grpSystem.Controls.Add(lblSysVol);
        grpSystem.Controls.Add(_trkSystemVolume);
        grpSystem.Controls.Add(_lblSystemVolume);
        grpSystem.Controls.Add(_btnSystemMute);
        grpSystem.Controls.Add(lblSysRoute);
        grpSystem.Controls.Add(_chkRouteSystemToVirtual);
        grpSystem.Controls.Add(_chkRouteSystemToPhysical);
        tab.Controls.Add(grpSystem);
        currentY += 238;

        // -------------------------------------------------------------
        // STRIP 2: Physical Microphone Input
        // -------------------------------------------------------------
        var grpMic = new GroupBox
        {
            Text = "INPUT 2: Physical Microphone (Speech & Voice Input)",
            Location = new Point(12, currentY),
            Size = new Size(800, 200)
        };

        var lblMicDev = new Label
        {
            Text = "Physical Microphone Device:",
            Location = new Point(15, 22),
            AutoSize = true
        };

        _cboMicrophoneDevice = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(15, 44),
            Width = 770,
            Font = new Font("Segoe UI", 9f)
        };

        var lblMicMeter = new Label { Text = "Live Level:", Location = new Point(15, 78), AutoSize = true };
        _prgMicrophoneLevel = new ProgressBar
        {
            Location = new Point(95, 78),
            Width = 290,
            Height = 16,
            Minimum = 0,
            Maximum = 100,
            Style = ProgressBarStyle.Continuous
        };

        _lblMicrophoneStatus = new Label
        {
            Text = "Status: Idle",
            Location = new Point(400, 78),
            Size = new Size(380, 18),
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = Color.DimGray
        };

        var lblMicVol = new Label { Text = "Gain / Vol:", Location = new Point(15, 108), AutoSize = true };
        _trkMicVolume = new TrackBar
        {
            Location = new Point(95, 102),
            Width = 290,
            Height = 30,
            Minimum = 0,
            Maximum = 200,
            Value = 100,
            TickFrequency = 25,
            SmallChange = 5,
            LargeChange = 20
        };
        _lblMicVolume = new Label { Text = "100%", Location = new Point(400, 108), AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
        _trkMicVolume.Scroll += (s, e) =>
        {
            _lblMicVolume.Text = $"{_trkMicVolume.Value}%";
            _overlay.AudioManager.MicrophoneVolume = _trkMicVolume.Value / 100.0f;
        };

        _btnMicMute = new Button
        {
            Text = "🔊 ON",
            Location = new Point(460, 104),
            Size = new Size(85, 28),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
        };
        _btnMicMute.Click += (s, e) =>
        {
            bool muted = !((bool)(_btnMicMute.Tag ?? false));
            UpdateMuteButtonVisual(_btnMicMute, muted);
            _overlay.AudioManager.MicrophoneMuted = muted;
        };

        var lblMicRoute = new Label
        {
            Text = "Routing Matrix:",
            Location = new Point(15, 146),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            AutoSize = true
        };

        _chkRouteMicToVirtual = new CheckBox
        {
            Text = "🔀 Route to Virtual Mic (Browser / ChatGPT)",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(79, 70, 229),
            Location = new Point(145, 144),
            AutoSize = true
        };
        _chkRouteMicToVirtual.CheckedChanged += (s, e) =>
        {
            _overlay.AudioManager.RouteMicToVirtual = _chkRouteMicToVirtual.Checked;
        };

        _chkRouteMicToPhysical = new CheckBox
        {
            Text = "🎧 Route to Headphones (Sidetone / Monitor)",
            Font = new Font("Segoe UI", 8.5f),
            Location = new Point(145, 168),
            AutoSize = true
        };
        _chkRouteMicToPhysical.CheckedChanged += (s, e) =>
        {
            _overlay.AudioManager.RouteMicToPhysical = _chkRouteMicToPhysical.Checked;
        };

        // Preserve legacy _cboAudioSource control for background compatibility
        _cboAudioSource = new ComboBox { Visible = false };
        _cboAudioSource.Items.Add("Computer / System Audio (WASAPI Loopback)");
        _cboAudioSource.Items.Add("Microphone");
        _cboAudioSource.Items.Add("Both (Computer Audio + Microphone)");

        grpMic.Controls.Add(lblMicDev);
        grpMic.Controls.Add(_cboMicrophoneDevice);
        grpMic.Controls.Add(lblMicMeter);
        grpMic.Controls.Add(_prgMicrophoneLevel);
        grpMic.Controls.Add(_lblMicrophoneStatus);
        grpMic.Controls.Add(lblMicVol);
        grpMic.Controls.Add(_trkMicVolume);
        grpMic.Controls.Add(_lblMicVolume);
        grpMic.Controls.Add(_btnMicMute);
        grpMic.Controls.Add(lblMicRoute);
        grpMic.Controls.Add(_chkRouteMicToVirtual);
        grpMic.Controls.Add(_chkRouteMicToPhysical);
        grpMic.Controls.Add(_cboAudioSource);
        tab.Controls.Add(grpMic);
        currentY += 210;

        // -------------------------------------------------------------
        // STRIP 3: Output Bus A - Virtual Audio Device (WebView2 / Browser)
        // -------------------------------------------------------------
        var grpVirtual = new GroupBox
        {
            Text = "OUTPUT BUS A: Virtual Audio Device -> Browser (Chromium getUserMedia)",
            Location = new Point(12, currentY),
            Size = new Size(800, 280)
        };

        _chkRouteToVirtualAudio = new CheckBox { Visible = false }; // Kept for API compatibility

        var lblVirtualDev = new Label
        {
            Text = "Target Virtual Playback Device (Bridge Destination):",
            Location = new Point(15, 22),
            AutoSize = true
        };

        _cboVirtualPlaybackDevice = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(15, 44),
            Width = 770,
            Font = new Font("Segoe UI", 9f)
        };
        _cboVirtualPlaybackDevice.SelectedIndexChanged += (s, e) => UpdatePairedCaptureAndFeedbackDisplay();

        _lblVirtualDriverInfo = new Label
        {
            Text = "Detecting virtual audio device...",
            Location = new Point(15, 74),
            Size = new Size(770, 18),
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = Color.DimGray,
            AutoEllipsis = true
        };

        _lblPairedCaptureInfo = new Label
        {
            Text = "Paired recording device: Detecting...",
            Location = new Point(15, 94),
            Size = new Size(770, 18),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
            ForeColor = Color.MidnightBlue,
            AutoEllipsis = true
        };

        _lblFeedbackWarning = new Label
        {
            Text = "⚠️ FEEDBACK PREVENTED: System audio source and virtual target are the same device!",
            Location = new Point(15, 114),
            Size = new Size(770, 18),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            ForeColor = Color.Crimson,
            Visible = false,
            AutoEllipsis = true
        };

        var lblBridgeLevelTitle = new Label { Text = "Bridge VU:", Location = new Point(15, 138), AutoSize = true };
        _prgBridgeLevel = new ProgressBar
        {
            Location = new Point(95, 138),
            Width = 290,
            Height = 16,
            Minimum = 0,
            Maximum = 100,
            Style = ProgressBarStyle.Continuous
        };

        _lblBridgeStatus = new Label
        {
            Text = "Status: Idle",
            Location = new Point(400, 138),
            Size = new Size(380, 18),
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = Color.DimGray
        };

        var lblVirtVol = new Label { Text = "Master Vol:", Location = new Point(15, 168), AutoSize = true };
        _trkVirtualVolume = new TrackBar
        {
            Location = new Point(95, 162),
            Width = 290,
            Height = 30,
            Minimum = 0,
            Maximum = 200,
            Value = 100,
            TickFrequency = 25,
            SmallChange = 5,
            LargeChange = 20
        };
        _lblVirtualVolume = new Label { Text = "100%", Location = new Point(400, 168), AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
        _trkVirtualVolume.Scroll += (s, e) =>
        {
            _lblVirtualVolume.Text = $"{_trkVirtualVolume.Value}%";
            _overlay.AudioManager.VirtualOutputVolume = _trkVirtualVolume.Value / 100.0f;
        };

        _btnVirtualMute = new Button
        {
            Text = "🔊 ON",
            Location = new Point(460, 164),
            Size = new Size(85, 28),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
        };
        _btnVirtualMute.Click += (s, e) =>
        {
            bool muted = !((bool)(_btnVirtualMute.Tag ?? false));
            UpdateMuteButtonVisual(_btnVirtualMute, muted);
            _overlay.AudioManager.VirtualOutputMuted = muted;
        };

        _chkAutoSwitchDefaultMic = new CheckBox
        {
            Text = "Auto-set paired virtual mic as Windows default (Keep OFF so Google Meet uses your physical mic)",
            Font = new Font("Segoe UI", 8.5f),
            Location = new Point(15, 202),
            AutoSize = true
        };

        _btnSetDefaultMic = new Button
        {
            Text = "🎯 Set Default Mic",
            Location = new Point(15, 230),
            Size = new Size(155, 30),
            Font = new Font("Segoe UI", 8.5f)
        };
        _btnSetDefaultMic.Click += (s, e) =>
        {
            var selectedVirtual = _cboVirtualPlaybackDevice.SelectedItem as AudioDeviceInfo;
            if (selectedVirtual != null && selectedVirtual.Id != "none")
            {
                var paired = AudioDeviceDiagnostics.FindPairedVirtualRecordingDevice(selectedVirtual.Id);
                if (paired != null)
                {
                    bool ok = AudioDeviceDiagnostics.SetDefaultRecordingDevice(paired.Id);
                    if (ok)
                        MessageBox.Show(this, $"Windows default recording device switched to:\r\n'{paired.Name}'", "Default Mic Switched", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    else
                        MessageBox.Show(this, "Could not automatically switch default recording device.", "Notice", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    UpdatePairedCaptureAndFeedbackDisplay();
                }
            }
        };

        _btnRestoreDefaultMic = new Button
        {
            Text = "↩️ Restore Original",
            Location = new Point(180, 230),
            Size = new Size(155, 30),
            Font = new Font("Segoe UI", 8.5f)
        };
        _btnRestoreDefaultMic.Click += (s, e) =>
        {
            bool ok = AudioDeviceDiagnostics.RestoreOriginalDefaultRecordingDevice();
            if (ok)
            {
                var def = AudioDeviceDiagnostics.GetDefaultRecordingDevice();
                MessageBox.Show(this, $"Restored Windows default recording device to:\r\n'{def?.Name ?? "Original Mic"}'", "Restored", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            UpdatePairedCaptureAndFeedbackDisplay();
        };

        _btnOpenMicTestPage = new Button
        {
            Text = "🎙️ Test in Browser",
            Location = new Point(345, 230),
            Size = new Size(155, 30),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(79, 70, 229)
        };
        _btnOpenMicTestPage.Click += (s, e) => _overlay.OpenMicrophoneTestPage();

        _btnOpenSoundControlPanel = new Button
        {
            Text = "🔊 Sound Panel",
            Location = new Point(510, 230),
            Size = new Size(150, 30),
            Font = new Font("Segoe UI", 8.5f)
        };
        _btnOpenSoundControlPanel.Click += (s, e) => AudioDeviceDiagnostics.OpenWindowsSoundRecordingControlPanel();

        grpVirtual.Controls.Add(_chkRouteToVirtualAudio);
        grpVirtual.Controls.Add(lblVirtualDev);
        grpVirtual.Controls.Add(_cboVirtualPlaybackDevice);
        grpVirtual.Controls.Add(_lblVirtualDriverInfo);
        grpVirtual.Controls.Add(_lblPairedCaptureInfo);
        grpVirtual.Controls.Add(_lblFeedbackWarning);
        grpVirtual.Controls.Add(lblBridgeLevelTitle);
        grpVirtual.Controls.Add(_prgBridgeLevel);
        grpVirtual.Controls.Add(_lblBridgeStatus);
        grpVirtual.Controls.Add(lblVirtVol);
        grpVirtual.Controls.Add(_trkVirtualVolume);
        grpVirtual.Controls.Add(_lblVirtualVolume);
        grpVirtual.Controls.Add(_btnVirtualMute);
        grpVirtual.Controls.Add(_chkAutoSwitchDefaultMic);
        grpVirtual.Controls.Add(_btnSetDefaultMic);
        grpVirtual.Controls.Add(_btnRestoreDefaultMic);
        grpVirtual.Controls.Add(_btnOpenMicTestPage);
        grpVirtual.Controls.Add(_btnOpenSoundControlPanel);
        tab.Controls.Add(grpVirtual);
        currentY += 290;

        // -------------------------------------------------------------
        // STRIP 4: Output Bus B - Physical Headphones / Speakers
        // -------------------------------------------------------------
        var grpPhysical = new GroupBox
        {
            Text = "OUTPUT BUS B: Physical Output (Headphones / Speakers / External)",
            Location = new Point(12, currentY),
            Size = new Size(800, 175)
        };

        var lblPhysDev = new Label
        {
            Text = "Physical Playback Output Device:",
            Location = new Point(15, 22),
            AutoSize = true
        };

        _cboPhysicalOutputDevice = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(15, 44),
            Width = 770,
            Font = new Font("Segoe UI", 9f)
        };
        _cboPhysicalOutputDevice.SelectedIndexChanged += (s, e) => UpdatePairedCaptureAndFeedbackDisplay();

        _lblPhysicalFeedbackWarning = new Label
        {
            Text = "⚠️ FEEDBACK PREVENTED: Cannot route system audio into the same physical output device being captured!",
            Location = new Point(15, 74),
            Size = new Size(770, 18),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            ForeColor = Color.Crimson,
            Visible = false,
            AutoEllipsis = true
        };

        var lblPhysMeter = new Label { Text = "Output VU:", Location = new Point(15, 98), AutoSize = true };
        _prgPhysicalOutputLevel = new ProgressBar
        {
            Location = new Point(95, 98),
            Width = 290,
            Height = 16,
            Minimum = 0,
            Maximum = 100,
            Style = ProgressBarStyle.Continuous
        };

        _lblPhysicalOutputStatus = new Label
        {
            Text = "Status: Output idle",
            Location = new Point(400, 98),
            Size = new Size(380, 18),
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = Color.DimGray
        };

        var lblPhysVol = new Label { Text = "Master Vol:", Location = new Point(15, 128), AutoSize = true };
        _trkPhysicalVolume = new TrackBar
        {
            Location = new Point(95, 122),
            Width = 290,
            Height = 30,
            Minimum = 0,
            Maximum = 200,
            Value = 100,
            TickFrequency = 25,
            SmallChange = 5,
            LargeChange = 20
        };
        _lblPhysicalVolume = new Label { Text = "100%", Location = new Point(400, 128), AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
        _trkPhysicalVolume.Scroll += (s, e) =>
        {
            _lblPhysicalVolume.Text = $"{_trkPhysicalVolume.Value}%";
            _overlay.AudioManager.PhysicalOutputVolume = _trkPhysicalVolume.Value / 100.0f;
        };

        _btnPhysicalMute = new Button
        {
            Text = "🔊 ON",
            Location = new Point(460, 124),
            Size = new Size(85, 28),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
        };
        _btnPhysicalMute.Click += (s, e) =>
        {
            bool muted = !((bool)(_btnPhysicalMute.Tag ?? false));
            UpdateMuteButtonVisual(_btnPhysicalMute, muted);
            _overlay.AudioManager.PhysicalOutputMuted = muted;
        };

        grpPhysical.Controls.Add(lblPhysDev);
        grpPhysical.Controls.Add(_cboPhysicalOutputDevice);
        grpPhysical.Controls.Add(_lblPhysicalFeedbackWarning);
        grpPhysical.Controls.Add(lblPhysMeter);
        grpPhysical.Controls.Add(_prgPhysicalOutputLevel);
        grpPhysical.Controls.Add(_lblPhysicalOutputStatus);
        grpPhysical.Controls.Add(lblPhysVol);
        grpPhysical.Controls.Add(_trkPhysicalVolume);
        grpPhysical.Controls.Add(_lblPhysicalVolume);
        grpPhysical.Controls.Add(_btnPhysicalMute);
        tab.Controls.Add(grpPhysical);
        currentY += 185;

        // -------------------------------------------------------------
        // STRIP 5: WebView2 Permissions & Diagnostics
        // -------------------------------------------------------------
        var grpPermissions = new GroupBox
        {
            Text = "5. WebView2 Browser Permissions & System Diagnostics",
            Location = new Point(12, currentY),
            Size = new Size(800, 115)
        };

        _chkAutoGrantMic = new CheckBox
        {
            Text = "Automatically grant microphone permissions to web apps (ChatGPT, Google Meet)",
            Font = new Font("Segoe UI", 8.5f),
            Location = new Point(15, 22),
            AutoSize = true
        };

        _lblPermissionStatus = new Label
        {
            Text = "Permission Tracker: Ready",
            Location = new Point(15, 48),
            Size = new Size(770, 18),
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = Color.DimGray
        };

        _btnAudioDiagnostics = new Button
        {
            Text = "📋 View Full VoiceMeeter Routing Diagnostics Report...",
            Location = new Point(15, 74),
            Size = new Size(420, 28),
            Font = new Font("Segoe UI", 8.5f)
        };
        _btnAudioDiagnostics.Click += (s, e) => ShowDiagnosticsReportDialog();

        grpPermissions.Controls.Add(_chkAutoGrantMic);
        grpPermissions.Controls.Add(_lblPermissionStatus);
        grpPermissions.Controls.Add(_btnAudioDiagnostics);
        tab.Controls.Add(grpPermissions);

        void UpdateAudioLayout()
        {
            int contentW = Math.Max(700, tab.ClientSize.Width - 28);
            lblBanner.Width = contentW;

            grpSystem.Width = contentW;
            _cboPlaybackDevice.Width = Math.Max(300, contentW - 145);
            _btnRefreshAudioDevices.Location = new Point(contentW - 125, 67);
            _lblSystemAudioStatus.Width = Math.Max(200, contentW - 415);

            grpMic.Width = contentW;
            _cboMicrophoneDevice.Width = Math.Max(300, contentW - 30);
            _lblMicrophoneStatus.Width = Math.Max(200, contentW - 415);

            grpVirtual.Width = contentW;
            _cboVirtualPlaybackDevice.Width = Math.Max(300, contentW - 30);
            _lblVirtualDriverInfo.Width = Math.Max(300, contentW - 30);
            _lblPairedCaptureInfo.Width = Math.Max(300, contentW - 30);
            _lblFeedbackWarning.Width = Math.Max(300, contentW - 30);
            _lblBridgeStatus.Width = Math.Max(200, contentW - 415);

            grpPhysical.Width = contentW;
            _cboPhysicalOutputDevice.Width = Math.Max(300, contentW - 30);
            _lblPhysicalFeedbackWarning.Width = Math.Max(300, contentW - 30);
            _lblPhysicalOutputStatus.Width = Math.Max(200, contentW - 415);

            grpPermissions.Width = contentW;
            _lblPermissionStatus.Width = Math.Max(300, contentW - 30);
        }
        tab.Resize += (s, e) => UpdateAudioLayout();
        UpdateAudioLayout();
    }

    private static void UpdateMuteButtonVisual(Button btn, bool isMuted)
    {
        btn.Tag = isMuted;
        if (isMuted)
        {
            btn.Text = "🔇 MUTED";
            btn.BackColor = Color.FromArgb(239, 68, 68);
            btn.ForeColor = Color.White;
        }
        else
        {
            btn.Text = "🔊 ON";
            btn.BackColor = Color.FromArgb(240, 240, 245);
            btn.ForeColor = Color.Black;
        }
    }

    private void BuildTranscriptionTab(TabPage tab)
    {
        tab.Padding = new Padding(12);
        tab.AutoScroll = true;

        var grpGeneral = new GroupBox
        {
            Text = "Continuous Background Audio-to-Text (Speech Recognition)",
            Location = new Point(12, 12),
            Size = new Size(820, 570)
        };

        var lblHelp = new Label
        {
            Text = "Continuously converts captured background/system audio into text and automatically types it into the active " +
                   "ChatGPT or Gemini message input in real time. Does NOT click Send, allowing you to review before submitting.\r\n" +
                   "Press Ctrl+Alt+T or click '🎙️ Transcribe' in the top bar to start/stop transcription anytime.",
            Location = new Point(15, 24),
            Size = new Size(785, 45),
            ForeColor = Color.FromArgb(99, 102, 241)
        };
        grpGeneral.Controls.Add(lblHelp);

        int y = 75;

        // 1. Engine Dropdown
        var lblEngine = new Label
        {
            Text = "Speech-to-Text Engine / Provider:",
            Location = new Point(15, y),
            AutoSize = true,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold)
        };
        grpGeneral.Controls.Add(lblEngine);

        y += 22;
        _cboSpeechEngine = new ComboBox
        {
            Location = new Point(15, y),
            Width = 520,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        _cboSpeechEngine.Items.Add("Windows Built-in Speech (Offline, Free - No API key needed)");
        _cboSpeechEngine.Items.Add("OpenAI Whisper API (Cloud - whisper-1)");
        _cboSpeechEngine.Items.Add("Groq Whisper API (Cloud - whisper-large-v3-turbo, Blazing Fast)");
        _cboSpeechEngine.Items.Add("Custom OpenAI-compatible Whisper Endpoint");
        _cboSpeechEngine.SelectedIndexChanged += (s, e) => UpdateEngineFieldsVisibility();
        grpGeneral.Controls.Add(_cboSpeechEngine);

        y += 35;

        // 2. API Key
        var lblApiKey = new Label
        {
            Text = "API Key (Kept local in your settings.json, never in source code):",
            Location = new Point(15, y),
            AutoSize = true
        };
        grpGeneral.Controls.Add(lblApiKey);

        y += 22;
        _txtSpeechApiKey = new TextBox
        {
            Location = new Point(15, y),
            Width = 680,
            UseSystemPasswordChar = true
        };
        _btnToggleApiKeyVisibility = new Button
        {
            Text = "👁 Show",
            Location = new Point(700, y - 1),
            Width = 75,
            Height = 25
        };
        _btnToggleApiKeyVisibility.Click += (s, e) =>
        {
            _txtSpeechApiKey.UseSystemPasswordChar = !_txtSpeechApiKey.UseSystemPasswordChar;
            _btnToggleApiKeyVisibility.Text = _txtSpeechApiKey.UseSystemPasswordChar ? "👁 Show" : "🔒 Hide";
        };
        grpGeneral.Controls.Add(_txtSpeechApiKey);
        grpGeneral.Controls.Add(_btnToggleApiKeyVisibility);

        y += 35;

        // 3. Endpoint URL
        var lblEndpoint = new Label
        {
            Text = "Endpoint URL:",
            Location = new Point(15, y),
            AutoSize = true
        };
        grpGeneral.Controls.Add(lblEndpoint);

        y += 22;
        _txtSpeechEndpoint = new TextBox
        {
            Location = new Point(15, y),
            Width = 760
        };
        grpGeneral.Controls.Add(_txtSpeechEndpoint);

        y += 35;

        // 4. Model and Language
        var lblModel = new Label
        {
            Text = "Model Name:",
            Location = new Point(15, y),
            AutoSize = true
        };
        grpGeneral.Controls.Add(lblModel);

        var lblLang = new Label
        {
            Text = "Language (e.g. 'en' or 'auto'):",
            Location = new Point(400, y),
            AutoSize = true
        };
        grpGeneral.Controls.Add(lblLang);

        y += 22;
        _txtSpeechModel = new TextBox
        {
            Location = new Point(15, y),
            Width = 360
        };
        grpGeneral.Controls.Add(_txtSpeechModel);

        _txtSpeechLanguage = new TextBox
        {
            Location = new Point(400, y),
            Width = 375
        };
        grpGeneral.Controls.Add(_txtSpeechLanguage);

        y += 35;

        // 5. Silence Threshold and Max Chunk Duration
        var lblThreshold = new Label
        {
            Text = "Voice Activity / Silence Sensitivity (Lower = more sensitive):",
            Location = new Point(15, y),
            AutoSize = true
        };
        grpGeneral.Controls.Add(lblThreshold);

        var lblChunkSec = new Label
        {
            Text = "Max Audio Segment Duration (3-12 seconds):",
            Location = new Point(400, y),
            AutoSize = true
        };
        grpGeneral.Controls.Add(lblChunkSec);

        y += 22;
        _numSilenceThreshold = new NumericUpDown
        {
            Location = new Point(15, y),
            Width = 360,
            DecimalPlaces = 3,
            Increment = 0.005m,
            Minimum = 0.005m,
            Maximum = 0.200m,
            Value = 0.015m
        };
        grpGeneral.Controls.Add(_numSilenceThreshold);

        _numMaxChunkSeconds = new NumericUpDown
        {
            Location = new Point(400, y),
            Width = 375,
            Minimum = 3,
            Maximum = 12,
            Value = 5
        };
        grpGeneral.Controls.Add(_numMaxChunkSeconds);

        y += 40;

        // 6. Test Button & Status
        _btnTestSpeechEngine = new Button
        {
            Text = "🧪 Test Engine Configuration",
            Location = new Point(15, y),
            Width = 230,
            Height = 32
        };
        _btnTestSpeechEngine.Click += (s, e) => TestCurrentSpeechEngine();
        grpGeneral.Controls.Add(_btnTestSpeechEngine);

        _lblSpeechStatus = new Label
        {
            Text = "Status: Ready",
            Location = new Point(260, y + 7),
            Size = new Size(515, 22),
            Font = new Font("Segoe UI", 9f, FontStyle.Italic)
        };
        grpGeneral.Controls.Add(_lblSpeechStatus);

        y += 45;

        var lblFooter = new Label
        {
            Text = "Supported AI Sites: ChatGPT (chatgpt.com), Google Gemini (gemini.google.com), Claude (claude.ai), Microsoft Copilot.\r\n" +
                   "System audio capture is securely performed via WASAPI loopback without routing into your physical or Meet mic.",
            Location = new Point(15, y),
            Size = new Size(785, 36),
            ForeColor = Color.DimGray
        };
        grpGeneral.Controls.Add(lblFooter);

        tab.Controls.Add(grpGeneral);
    }

    private void UpdateEngineFieldsVisibility()
    {
        int index = _cboSpeechEngine.SelectedIndex;
        if (index == 0) // Windows Built-in
        {
            _txtSpeechApiKey.Enabled = false;
            _btnToggleApiKeyVisibility.Enabled = false;
            _txtSpeechEndpoint.Enabled = false;
            _txtSpeechModel.Enabled = false;
            _lblSpeechStatus.Text = "Status: Windows Built-in Speech Recognizer selected (Offline, Free - No API key needed).";
            _lblSpeechStatus.ForeColor = Color.DarkSlateBlue;
        }
        else if (index == 1) // OpenAI Whisper
        {
            _txtSpeechApiKey.Enabled = true;
            _btnToggleApiKeyVisibility.Enabled = true;
            _txtSpeechEndpoint.Enabled = true;
            _txtSpeechModel.Enabled = true;
            if (string.IsNullOrWhiteSpace(_txtSpeechEndpoint.Text) || _txtSpeechEndpoint.Text.Contains("groq.com"))
            {
                _txtSpeechEndpoint.Text = "https://api.openai.com/v1/audio/transcriptions";
            }
            if (string.IsNullOrWhiteSpace(_txtSpeechModel.Text) || _txtSpeechModel.Text.Contains("large"))
            {
                _txtSpeechModel.Text = "whisper-1";
            }
            _lblSpeechStatus.Text = string.IsNullOrWhiteSpace(_txtSpeechApiKey.Text)
                ? "Status: API Key required for OpenAI Whisper."
                : "Status: OpenAI Whisper configured.";
            _lblSpeechStatus.ForeColor = string.IsNullOrWhiteSpace(_txtSpeechApiKey.Text) ? Color.Crimson : Color.ForestGreen;
        }
        else if (index == 2) // Groq Whisper
        {
            _txtSpeechApiKey.Enabled = true;
            _btnToggleApiKeyVisibility.Enabled = true;
            _txtSpeechEndpoint.Enabled = true;
            _txtSpeechModel.Enabled = true;
            _txtSpeechEndpoint.Text = "https://api.groq.com/openai/v1/audio/transcriptions";
            _txtSpeechModel.Text = "whisper-large-v3-turbo";
            _lblSpeechStatus.Text = string.IsNullOrWhiteSpace(_txtSpeechApiKey.Text)
                ? "Status: API Key required for Groq Whisper."
                : "Status: Groq Whisper configured.";
            _lblSpeechStatus.ForeColor = string.IsNullOrWhiteSpace(_txtSpeechApiKey.Text) ? Color.Crimson : Color.ForestGreen;
        }
        else // Custom
        {
            _txtSpeechApiKey.Enabled = true;
            _btnToggleApiKeyVisibility.Enabled = true;
            _txtSpeechEndpoint.Enabled = true;
            _txtSpeechModel.Enabled = true;
            _lblSpeechStatus.Text = "Status: Custom endpoint selected.";
            _lblSpeechStatus.ForeColor = Color.DarkSlateBlue;
        }
    }

    private void TestCurrentSpeechEngine()
    {
        var tempSettings = new AppSettings
        {
            SpeechToTextApiKey = _txtSpeechApiKey.Text.Trim(),
            SpeechToTextEndpoint = _txtSpeechEndpoint.Text.Trim(),
            SpeechToTextModel = _txtSpeechModel.Text.Trim(),
            SpeechToTextLanguage = _txtSpeechLanguage.Text.Trim(),
            TranscriptionSilenceThreshold = (float)_numSilenceThreshold.Value,
            TranscriptionMaxChunkSeconds = (int)_numMaxChunkSeconds.Value
        };

        switch (_cboSpeechEngine.SelectedIndex)
        {
            case 0:
                tempSettings.SpeechToTextEngine = SpeechEngineFactory.EngineWindows;
                break;
            case 1:
                tempSettings.SpeechToTextEngine = SpeechEngineFactory.EngineOpenAI;
                break;
            case 2:
                tempSettings.SpeechToTextEngine = SpeechEngineFactory.EngineGroq;
                break;
            case 3:
                tempSettings.SpeechToTextEngine = SpeechEngineFactory.EngineCustom;
                break;
        }

        using var engine = SpeechEngineFactory.CreateEngine(tempSettings);

        if (!engine.IsConfigured)
        {
            _lblSpeechStatus.Text = $"⚠️ {engine.ConfigurationErrorDescription}";
            _lblSpeechStatus.ForeColor = Color.Crimson;
            MessageBox.Show(this, engine.ConfigurationErrorDescription, "Speech-to-Text Configuration Notice", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        else
        {
            _lblSpeechStatus.Text = $"✓ {engine.Name} is configured and ready!";
            _lblSpeechStatus.ForeColor = Color.ForestGreen;
            MessageBox.Show(this, $"{engine.Name} is configured and ready to transcribe background audio.\r\n\r\nShortcut: Ctrl+Alt+T toggles continuous transcription.", "Engine Ready", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void PopulateAudioDeviceDropdowns()
    {
        // 1. Playback Devices (WASAPI Loopback source)
        string? currentSelectedId = (_cboPlaybackDevice.SelectedItem as AudioDeviceInfo)?.Id ?? _settings.SystemAudioDeviceId;
        _cboPlaybackDevice.Items.Clear();

        var defaultPlaybackItem = new AudioDeviceInfo
        {
            Id = "default",
            Name = "[Default] Windows Default Output (Follows wired earphones/speakers)",
            IsDefault = true,
            IsPlayback = true,
            Classification = AudioDeviceClassification.SpeakersOrHeadphones
        };
        _cboPlaybackDevice.Items.Add(defaultPlaybackItem);

        var playbackDevices = SystemAudioCapture.GetPlaybackDevices();
        int selectedIndex = 0;

        for (int i = 0; i < playbackDevices.Count; i++)
        {
            var dev = playbackDevices[i];
            _cboPlaybackDevice.Items.Add(dev);

            if (!string.IsNullOrEmpty(currentSelectedId) &&
                dev.Id.Equals(currentSelectedId, StringComparison.OrdinalIgnoreCase))
            {
                selectedIndex = i + 1;
            }
        }

        if (string.IsNullOrEmpty(currentSelectedId) || currentSelectedId.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            selectedIndex = 0;
        }

        if (_cboPlaybackDevice.Items.Count > 0)
        {
            _cboPlaybackDevice.SelectedIndex = Math.Clamp(selectedIndex, 0, _cboPlaybackDevice.Items.Count - 1);
        }

        // 2. Virtual Playback Devices (Bridge target - Output 1)
        string? currentVirtualId = (_cboVirtualPlaybackDevice.SelectedItem as AudioDeviceInfo)?.Id ?? _settings.VirtualAudioPlaybackDeviceId;
        _cboVirtualPlaybackDevice.Items.Clear();

        var offVirtualItem = new AudioDeviceInfo
        {
            Id = "off",
            Name = "Off",
            IsPlayback = true,
            Classification = AudioDeviceClassification.Disabled
        };
        _cboVirtualPlaybackDevice.Items.Add(offVirtualItem);

        var virtualPlaybacks = AudioDeviceDiagnostics.GetVirtualPlaybackDevices();
        int virtualIndex = 0; // Default to Off (index 0)

        for (int i = 0; i < virtualPlaybacks.Count; i++)
        {
            var vdev = virtualPlaybacks[i];
            _cboVirtualPlaybackDevice.Items.Add(vdev);

            if (!string.IsNullOrEmpty(currentVirtualId) &&
                vdev.Id.Equals(currentVirtualId, StringComparison.OrdinalIgnoreCase))
            {
                virtualIndex = i + 1; // offset by 1 because Off is index 0
            }
        }

        if (virtualPlaybacks.Count == 0)
        {
            var noVirtualItem = new AudioDeviceInfo
            {
                Id = "none",
                Name = "(No virtual audio device detected - see driver notice below)",
                IsPlayback = true,
                Classification = AudioDeviceClassification.Disabled
            };
            _cboVirtualPlaybackDevice.Items.Add(noVirtualItem);

            _lblVirtualDriverInfo.Text = "⚠️ No virtual audio device detected. Install VB-Audio Virtual CABLE (free from vb-audio.com) to route system audio into WebView2.";
            _lblVirtualDriverInfo.ForeColor = Color.DarkGoldenrod;
            _lblPairedCaptureInfo.Text = "Paired recording device: None available";
        }
        else if (virtualIndex > 0)
        {
            _lblVirtualDriverInfo.Text = $"Compatible virtual audio device ready: {virtualPlaybacks.Count} endpoint(s) detected.";
            _lblVirtualDriverInfo.ForeColor = Color.ForestGreen;
        }

        if (string.IsNullOrEmpty(currentVirtualId) ||
            currentVirtualId.Equals("off", StringComparison.OrdinalIgnoreCase) ||
            currentVirtualId.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            virtualIndex = 0;
        }

        if (_cboVirtualPlaybackDevice.Items.Count > 0)
        {
            _cboVirtualPlaybackDevice.SelectedIndex = Math.Clamp(virtualIndex, 0, _cboVirtualPlaybackDevice.Items.Count - 1);
        }

        // 3. Microphone Devices (Physical input)
        string? currentMicId = (_cboMicrophoneDevice.SelectedItem as AudioDeviceInfo)?.Id ?? _settings.MicrophoneDeviceId;
        _cboMicrophoneDevice.Items.Clear();

        var defaultMicItem = new AudioDeviceInfo
        {
            Id = "default",
            Name = "[Default] Windows Default Microphone",
            IsDefault = true,
            IsPlayback = false,
            Classification = AudioDeviceClassification.PhysicalMicrophone
        };
        _cboMicrophoneDevice.Items.Add(defaultMicItem);

        var captureDevices = MicrophoneAudioCapture.GetCaptureDevices();
        int micIndex = 0;

        for (int i = 0; i < captureDevices.Count; i++)
        {
            var dev = captureDevices[i];
            _cboMicrophoneDevice.Items.Add(dev);

            if (!string.IsNullOrEmpty(currentMicId) &&
                dev.Id.Equals(currentMicId, StringComparison.OrdinalIgnoreCase))
            {
                micIndex = i + 1;
            }
        }

        if (string.IsNullOrEmpty(currentMicId) || currentMicId.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            micIndex = 0;
        }

        if (_cboMicrophoneDevice.Items.Count > 0)
        {
            _cboMicrophoneDevice.SelectedIndex = Math.Clamp(micIndex, 0, _cboMicrophoneDevice.Items.Count - 1);
        }

        // 4. Physical Playback Output Devices (Headphones / Speakers - Output 2)
        string? currentPhysicalId = (_cboPhysicalOutputDevice.SelectedItem as AudioDeviceInfo)?.Id ?? _settings.PhysicalOutputDeviceId;
        _cboPhysicalOutputDevice.Items.Clear();

        var offPhysicalItem = new AudioDeviceInfo
        {
            Id = "off",
            Name = "Off",
            IsPlayback = true,
            Classification = AudioDeviceClassification.Disabled
        };
        _cboPhysicalOutputDevice.Items.Add(offPhysicalItem);

        var defaultPhysicalItem = new AudioDeviceInfo
        {
            Id = "default",
            Name = "[Default] Windows Default Output (Headphones/Speakers)",
            IsDefault = true,
            IsPlayback = true,
            Classification = AudioDeviceClassification.SpeakersOrHeadphones
        };
        _cboPhysicalOutputDevice.Items.Add(defaultPhysicalItem);

        int physicalIndex = 0; // Default to Off (index 0)
        for (int i = 0; i < playbackDevices.Count; i++)
        {
            var dev = playbackDevices[i];
            // Skip known virtual devices in physical playback output to avoid feedback
            if (AudioDeviceDiagnostics.IsKnownVirtualAudioDevice(dev.Name, dev.Id)) continue;

            _cboPhysicalOutputDevice.Items.Add(dev);
            if (!string.IsNullOrEmpty(currentPhysicalId) &&
                dev.Id.Equals(currentPhysicalId, StringComparison.OrdinalIgnoreCase))
            {
                physicalIndex = _cboPhysicalOutputDevice.Items.Count - 1;
            }
        }

        if (!string.IsNullOrEmpty(currentPhysicalId) && currentPhysicalId.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            physicalIndex = 1;
        }
        else if (string.IsNullOrEmpty(currentPhysicalId) ||
                 currentPhysicalId.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                 currentPhysicalId.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            physicalIndex = 0;
        }

        if (_cboPhysicalOutputDevice.Items.Count > 0)
        {
            _cboPhysicalOutputDevice.SelectedIndex = Math.Clamp(physicalIndex, 0, _cboPhysicalOutputDevice.Items.Count - 1);
        }

        UpdatePairedCaptureAndFeedbackDisplay();
    }

    private void UpdatePairedCaptureAndFeedbackDisplay()
    {
        var selectedVirtual = _cboVirtualPlaybackDevice?.SelectedItem as AudioDeviceInfo;
        bool isVirtualOff = selectedVirtual == null ||
                            selectedVirtual.Id.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                            selectedVirtual.Id.Equals("none", StringComparison.OrdinalIgnoreCase);

        if (!isVirtualOff)
        {
            var paired = AudioDeviceDiagnostics.FindPairedVirtualRecordingDevice(selectedVirtual!.Id);
            if (paired != null)
            {
                var defRecording = AudioDeviceDiagnostics.GetDefaultRecordingDevice();
                bool isDef = defRecording != null && paired.Id.Equals(defRecording.Id, StringComparison.OrdinalIgnoreCase);
                _lblPairedCaptureInfo.Text = $"Paired browser mic: '{paired.Name}' " +
                    (isDef ? "✓ [Current Windows Default Mic]" : "⚠️ [NOT Default - Click below or set in Sound Settings]");
                _lblPairedCaptureInfo.ForeColor = isDef ? Color.DarkGreen : Color.MidnightBlue;

                if (_btnSetDefaultMic != null) _btnSetDefaultMic.Enabled = !isDef;
                if (_btnRestoreDefaultMic != null) _btnRestoreDefaultMic.Enabled = AudioDeviceDiagnostics.HasSwitchedDefaultRecordingDevice || !string.IsNullOrEmpty(AudioDeviceDiagnostics.OriginalDefaultRecordingDeviceId);
            }
            else
            {
                _lblPairedCaptureInfo.Text = "Paired browser mic: None detected for this device.";
                _lblPairedCaptureInfo.ForeColor = Color.DimGray;
                if (_btnSetDefaultMic != null) _btnSetDefaultMic.Enabled = false;
                if (_btnRestoreDefaultMic != null) _btnRestoreDefaultMic.Enabled = AudioDeviceDiagnostics.HasSwitchedDefaultRecordingDevice || !string.IsNullOrEmpty(AudioDeviceDiagnostics.OriginalDefaultRecordingDeviceId);
            }

            // Check feedback loop for virtual bridge
            var selectedPlayback = _cboPlaybackDevice?.SelectedItem as AudioDeviceInfo;
            string? sourceId = selectedPlayback?.Id;
            bool isFeedback = AudioDeviceDiagnostics.IsFeedbackLoop(sourceId, selectedVirtual.Id);
            if (_lblFeedbackWarning != null) _lblFeedbackWarning.Visible = isFeedback;
        }
        else
        {
            _lblVirtualDriverInfo.Text = "Virtual audio output is Off. System audio will not be routed to browser/virtual mic.";
            _lblVirtualDriverInfo.ForeColor = Color.DimGray;
            _lblPairedCaptureInfo.Text = "Paired recording device: Output Off";
            _lblPairedCaptureInfo.ForeColor = Color.DimGray;
            if (_lblBridgeStatus != null) _lblBridgeStatus.Text = "Status: Output disabled (Off)";
            if (_prgBridgeLevel != null) _prgBridgeLevel.Value = 0;
            if (_lblFeedbackWarning != null) _lblFeedbackWarning.Visible = false;
            if (_btnSetDefaultMic != null) _btnSetDefaultMic.Enabled = false;
            if (_btnRestoreDefaultMic != null) _btnRestoreDefaultMic.Enabled = AudioDeviceDiagnostics.HasSwitchedDefaultRecordingDevice || !string.IsNullOrEmpty(AudioDeviceDiagnostics.OriginalDefaultRecordingDeviceId);
        }

        // Check feedback loop for physical bridge
        if (_cboPhysicalOutputDevice != null)
        {
            var selectedPlayback = _cboPlaybackDevice?.SelectedItem as AudioDeviceInfo;
            var selectedPhysical = _cboPhysicalOutputDevice.SelectedItem as AudioDeviceInfo;
            bool isPhysicalOff = selectedPhysical == null ||
                                 selectedPhysical.Id.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                                 selectedPhysical.Id.Equals("none", StringComparison.OrdinalIgnoreCase);

            if (isPhysicalOff)
            {
                if (_lblPhysicalFeedbackWarning != null) _lblPhysicalFeedbackWarning.Visible = false;
                if (_lblPhysicalOutputStatus != null) _lblPhysicalOutputStatus.Text = "Status: Output disabled (Off)";
                if (_prgPhysicalOutputLevel != null) _prgPhysicalOutputLevel.Value = 0;
            }
            else
            {
                bool physicalFeedback = selectedPlayback != null &&
                    AudioDeviceDiagnostics.IsFeedbackLoop(selectedPlayback.Id, selectedPhysical!.Id) &&
                    _chkRouteSystemToPhysical.Checked;
                if (_lblPhysicalFeedbackWarning != null) _lblPhysicalFeedbackWarning.Visible = physicalFeedback;
            }
        }
    }

    private void ShowDiagnosticsReportDialog()
    {
        string report = AudioDeviceDiagnostics.GetAudioRoutingDiagnosticsReport(_overlay.AudioManager, _settings);
        using var diagForm = new Form
        {
            Text = "Audio Routing & Diagnostics Report",
            Size = new Size(680, 560),
            StartPosition = FormStartPosition.CenterParent,
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.Sizable,
            MinimizeBox = false,
            MaximizeBox = true
        };
        ScreenCaptureProtection.ProtectForm(diagForm);

        var txt = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9f),
            Text = report
        };

        var pnlBottom = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 45,
            Padding = new Padding(8)
        };

        var btnCopy = new Button
        {
            Text = "Copy to Clipboard",
            Width = 140,
            Height = 28,
            Location = new Point(8, 8)
        };
        btnCopy.Click += (s, e) =>
        {
            try
            {
                Clipboard.SetText(txt.Text);
                MessageBox.Show(diagForm, "Report copied to clipboard!", "Copied", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(diagForm, $"Failed to copy: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };

        var btnClose = new Button
        {
            Text = "Close",
            Width = 80,
            Height = 28,
            Location = new Point(diagForm.ClientSize.Width - 95, 8),
            Anchor = AnchorStyles.Right | AnchorStyles.Top
        };
        btnClose.Click += (s, e) => diagForm.Close();

        pnlBottom.Controls.Add(btnCopy);
        pnlBottom.Controls.Add(btnClose);
        diagForm.Controls.Add(txt);
        diagForm.Controls.Add(pnlBottom);

        diagForm.ShowDialog(this);
    }

    private void OnSystemAudioLevelChanged(object? sender, float level)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(() =>
            {
                if (!IsDisposed && _prgSystemAudioLevel != null && _prgSystemAudioLevel.IsHandleCreated)
                {
                    _prgSystemAudioLevel.Value = Math.Clamp((int)(level * 100), 0, 100);
                }
            });
        }
        catch {}
    }

    private void OnSystemAudioStatusChanged(object? sender, string status)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(() =>
            {
                if (!IsDisposed && _lblSystemAudioStatus != null && _lblSystemAudioStatus.IsHandleCreated)
                {
                    _lblSystemAudioStatus.Text = $"Status: {status}";
                }
            });
        }
        catch {}
    }

    private void OnBridgeLevelChanged(object? sender, float level)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(() =>
            {
                if (!IsDisposed && _prgBridgeLevel != null && _prgBridgeLevel.IsHandleCreated)
                {
                    _prgBridgeLevel.Value = Math.Clamp((int)(level * 100), 0, 100);
                }
            });
        }
        catch {}
    }

    private void OnBridgeStatusChanged(object? sender, string status)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(() =>
            {
                if (!IsDisposed && _lblBridgeStatus != null && _lblBridgeStatus.IsHandleCreated)
                {
                    _lblBridgeStatus.Text = $"Status: {status}";
                }
            });
        }
        catch {}
    }

    private void OnPermissionEventLogged(object? sender, WebView2PermissionRecord record)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(() =>
            {
                if (!IsDisposed && _lblPermissionStatus != null && _lblPermissionStatus.IsHandleCreated)
                {
                    _lblPermissionStatus.Text = $"Permission Tracker: {record}";
                }
            });
        }
        catch {}
    }

    private void OnMicrophoneLevelChanged(object? sender, float level)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(() =>
            {
                if (!IsDisposed && _prgMicrophoneLevel != null && _prgMicrophoneLevel.IsHandleCreated)
                {
                    _prgMicrophoneLevel.Value = Math.Clamp((int)(level * 100), 0, 100);
                }
            });
        }
        catch {}
    }

    private void OnMicrophoneStatusChanged(object? sender, string status)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(() =>
            {
                if (!IsDisposed && _lblMicrophoneStatus != null && _lblMicrophoneStatus.IsHandleCreated)
                {
                    _lblMicrophoneStatus.Text = $"Status: {status}";
                }
            });
        }
        catch {}
    }

    private void OnPhysicalLevelChanged(object? sender, float level)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(() =>
            {
                if (!IsDisposed && _prgPhysicalOutputLevel != null && _prgPhysicalOutputLevel.IsHandleCreated)
                {
                    _prgPhysicalOutputLevel.Value = Math.Clamp((int)(level * 100), 0, 100);
                }
            });
        }
        catch {}
    }

    private void OnPhysicalStatusChanged(object? sender, string status)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(() =>
            {
                if (!IsDisposed && _lblPhysicalOutputStatus != null && _lblPhysicalOutputStatus.IsHandleCreated)
                {
                    _lblPhysicalOutputStatus.Text = $"Status: {status}";
                }
            });
        }
        catch {}
    }

    private void OnAudioDevicesChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(PopulateAudioDeviceDropdowns);
        }
        catch {}
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _overlay.AudioManager.SystemAudioLevelChanged -= OnSystemAudioLevelChanged;
        _overlay.AudioManager.SystemAudio.StatusChanged -= OnSystemAudioStatusChanged;
        _overlay.AudioManager.MicrophoneLevelChanged -= OnMicrophoneLevelChanged;
        _overlay.AudioManager.Microphone.StatusChanged -= OnMicrophoneStatusChanged;
        _overlay.AudioManager.SystemAudio.DeviceChanged -= OnAudioDevicesChanged;

        _overlay.AudioManager.VirtualBridge.BridgeLevelChanged -= OnBridgeLevelChanged;
        _overlay.AudioManager.VirtualBridge.StatusChanged -= OnBridgeStatusChanged;
        _overlay.AudioManager.PhysicalBridge.BridgeLevelChanged -= OnPhysicalLevelChanged;
        _overlay.AudioManager.PhysicalBridge.StatusChanged -= OnPhysicalStatusChanged;
        WebView2AudioPermissionTracker.PermissionEventLogged -= OnPermissionEventLogged;

        base.OnFormClosed(e);
    }

    private void UpdateFontPreviewLabel()
    {
        string style = _selectedFont.Bold ? "Bold" : (_selectedFont.Italic ? "Italic" : "Regular");
        _lblFontPreview.Text = $"{_selectedFont.FontFamily.Name}, {_selectedFont.SizeInPoints:0.#}pt ({style})";
    }

    private void LoadValuesFromSettings()
    {
        _txtHomePage.Text = _settings.BrowserHomePage;
        _txtSearchTemplate.Text = _settings.SearchEngineTemplate;
        _chkShowBrowserStartup.Checked = _settings.ShowBrowser;

        _txtOverlayText.Text = _settings.Text;

        UpdateFontPreviewLabel();
        _pnlTextColor.BackColor = _selectedTextColor;
        _pnlBgColor.BackColor = _selectedBgColor;
        _pnlBorderColor.BackColor = _selectedBorderColor;

        int opacityPct = Math.Clamp((int)(_settings.Opacity * 100), 10, 100);
        _trkOpacity.Value = opacityPct;
        _lblOpacityValue.Text = $"{opacityPct}%";

        _numX.Value = _settings.X;
        _numY.Value = _settings.Y;
        _numWidth.Value = Math.Max(200, _settings.Width);
        _numHeight.Value = Math.Max(150, _settings.Height);

        _chkAlwaysOnTop.Checked = _settings.AlwaysOnTop;
        _chkClickThrough.Checked = _settings.ClickThrough;
        _chkShowBorder.Checked = _settings.ShowBorder;
        _chkScreenCaptureProtection.Checked = _settings.ScreenCaptureProtection;

        // Audio Settings & Matrix Routing
        _chkCaptureSystemAudio.Checked = _settings.CaptureSystemAudio;
        _chkRouteToVirtualAudio.Checked = _settings.RouteToVirtualAudio;
        _chkAutoSwitchDefaultMic.Checked = _settings.AutoSwitchDefaultRecordingDevice;
        _chkAutoGrantMic.Checked = _settings.AutoGrantMicrophonePermission;

        _chkRouteSystemToVirtual.Checked = _settings.RouteSystemToVirtual;
        _chkRouteSystemToPhysical.Checked = _settings.RouteSystemToPhysical;
        _chkRouteMicToVirtual.Checked = _settings.RouteMicToVirtual;
        _chkRouteMicToPhysical.Checked = _settings.RouteMicToPhysical;

        // Volumes & Mute
        _trkSystemVolume.Value = Math.Clamp((int)(_settings.SystemAudioVolume * 100), 0, 200);
        _lblSystemVolume.Text = $"{_trkSystemVolume.Value}%";
        UpdateMuteButtonVisual(_btnSystemMute, _settings.SystemAudioMuted);

        _trkMicVolume.Value = Math.Clamp((int)(_settings.MicrophoneVolume * 100), 0, 200);
        _lblMicVolume.Text = $"{_trkMicVolume.Value}%";
        UpdateMuteButtonVisual(_btnMicMute, _settings.MicrophoneMuted);

        _trkVirtualVolume.Value = Math.Clamp((int)(_settings.VirtualOutputVolume * 100), 0, 200);
        _lblVirtualVolume.Text = $"{_trkVirtualVolume.Value}%";
        UpdateMuteButtonVisual(_btnVirtualMute, _settings.VirtualOutputMuted);

        _trkPhysicalVolume.Value = Math.Clamp((int)(_settings.PhysicalOutputVolume * 100), 0, 200);
        _lblPhysicalVolume.Text = $"{_trkPhysicalVolume.Value}%";
        UpdateMuteButtonVisual(_btnPhysicalMute, _settings.PhysicalOutputMuted);

        PopulateAudioDeviceDropdowns();

        switch (_settings.AudioSource)
        {
            case AudioSourceMode.SystemAudioOnly:
                _cboAudioSource.SelectedIndex = 0;
                break;
            case AudioSourceMode.MicrophoneOnly:
                _cboAudioSource.SelectedIndex = 1;
                break;
            case AudioSourceMode.Both:
                _cboAudioSource.SelectedIndex = 2;
                break;
        }

        if (_overlay.AudioManager.SystemAudio.IsCapturing)
        {
            _lblSystemAudioStatus.Text = $"Status: Capturing ({_overlay.AudioManager.SystemAudio.CurrentDeviceName ?? "Default"})";
        }
        else
        {
            _lblSystemAudioStatus.Text = _settings.CaptureSystemAudio ? "Status: Initializing..." : "Status: Capture disabled";
        }

        if (_overlay.AudioManager.VirtualBridge.IsActive)
        {
            _lblBridgeStatus.Text = $"Status: Active -> {_overlay.AudioManager.VirtualBridge.TargetDeviceName ?? "Virtual Audio"}";
        }
        else
        {
            bool isVirtOff = string.IsNullOrWhiteSpace(_settings.VirtualAudioPlaybackDeviceId) ||
                             _settings.VirtualAudioPlaybackDeviceId.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                             _settings.VirtualAudioPlaybackDeviceId.Equals("none", StringComparison.OrdinalIgnoreCase);

            if (isVirtOff)
            {
                _lblBridgeStatus.Text = "Status: Output disabled (Off)";
            }
            else
            {
                _lblBridgeStatus.Text = (_settings.RouteSystemToVirtual || _settings.RouteMicToVirtual || _settings.RouteToVirtualAudio)
                    ? "Status: Initializing..."
                    : "Status: Bridge stopped";
            }
        }

        if (_overlay.AudioManager.PhysicalBridge.IsActive)
        {
            _lblPhysicalOutputStatus.Text = $"Status: Active -> {_overlay.AudioManager.PhysicalBridge.TargetDeviceName ?? "Headphones"}";
        }
        else
        {
            bool isPhysOff = string.IsNullOrWhiteSpace(_settings.PhysicalOutputDeviceId) ||
                             _settings.PhysicalOutputDeviceId.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                             _settings.PhysicalOutputDeviceId.Equals("none", StringComparison.OrdinalIgnoreCase);

            if (isPhysOff)
            {
                _lblPhysicalOutputStatus.Text = "Status: Output disabled (Off)";
            }
            else
            {
                _lblPhysicalOutputStatus.Text = (_settings.RouteSystemToPhysical || _settings.RouteMicToPhysical)
                    ? "Status: Initializing..."
                    : "Status: Output idle";
            }
        }

        _lblPermissionStatus.Text = $"Permission Tracker: {WebView2AudioPermissionTracker.GetStatusDescription()}";

        // Load Continuous Audio-to-Text Settings
        string engine = _settings.SpeechToTextEngine ?? SpeechEngineFactory.EngineWindows;
        if (engine.Equals(SpeechEngineFactory.EngineOpenAI, StringComparison.OrdinalIgnoreCase))
            _cboSpeechEngine.SelectedIndex = 1;
        else if (engine.Equals(SpeechEngineFactory.EngineGroq, StringComparison.OrdinalIgnoreCase))
            _cboSpeechEngine.SelectedIndex = 2;
        else if (engine.Equals(SpeechEngineFactory.EngineCustom, StringComparison.OrdinalIgnoreCase))
            _cboSpeechEngine.SelectedIndex = 3;
        else
            _cboSpeechEngine.SelectedIndex = 0;

        _txtSpeechApiKey.Text = _settings.SpeechToTextApiKey ?? "";
        _txtSpeechEndpoint.Text = _settings.SpeechToTextEndpoint ?? "https://api.openai.com/v1/audio/transcriptions";
        _txtSpeechModel.Text = _settings.SpeechToTextModel ?? "whisper-1";
        _txtSpeechLanguage.Text = _settings.SpeechToTextLanguage ?? "en";
        _numSilenceThreshold.Value = (decimal)Math.Clamp(_settings.TranscriptionSilenceThreshold, 0.005f, 0.200f);
        _numMaxChunkSeconds.Value = Math.Clamp(_settings.TranscriptionMaxChunkSeconds, 3, 12);
        UpdateEngineFieldsVisibility();
    }

    private void ApplySettings(bool saveToDisk)
    {
        _settings.BrowserHomePage = _txtHomePage.Text.Trim();
        _settings.SearchEngineTemplate = _txtSearchTemplate.Text.Trim();
        _settings.ShowBrowser = _chkShowBrowserStartup.Checked;

        _settings.Text = _txtOverlayText.Text;

        _settings.FontFamily = _selectedFont.FontFamily.Name;
        _settings.FontSize = _selectedFont.SizeInPoints;
        _settings.FontBold = _selectedFont.Bold;
        _settings.FontItalic = _selectedFont.Italic;

        _settings.TextColorHex = ColorTranslator.ToHtml(_selectedTextColor);
        _settings.BackgroundColorHex = ColorTranslator.ToHtml(_selectedBgColor);
        _settings.BorderColorHex = ColorTranslator.ToHtml(_selectedBorderColor);

        _settings.Opacity = _trkOpacity.Value / 100.0;

        _settings.X = (int)_numX.Value;
        _settings.Y = (int)_numY.Value;
        _settings.Width = (int)_numWidth.Value;
        _settings.Height = (int)_numHeight.Value;

        _settings.AlwaysOnTop = _chkAlwaysOnTop.Checked;
        _settings.ClickThrough = _chkClickThrough.Checked;
        _settings.ShowBorder = _chkShowBorder.Checked;
        _settings.ScreenCaptureProtection = _chkScreenCaptureProtection.Checked;

        // Audio Settings & VoiceMeeter Matrix
        _settings.CaptureSystemAudio = _chkCaptureSystemAudio.Checked;
        _settings.RouteSystemToVirtual = _chkRouteSystemToVirtual.Checked;
        _settings.RouteSystemToPhysical = _chkRouteSystemToPhysical.Checked;
        _settings.RouteMicToVirtual = _chkRouteMicToVirtual.Checked;
        _settings.RouteMicToPhysical = _chkRouteMicToPhysical.Checked;
        _settings.RouteToVirtualAudio = _chkRouteSystemToVirtual.Checked || _chkRouteMicToVirtual.Checked;

        _settings.SystemAudioVolume = _trkSystemVolume.Value / 100.0f;
        _settings.SystemAudioMuted = (bool)(_btnSystemMute.Tag ?? false);

        _settings.MicrophoneVolume = _trkMicVolume.Value / 100.0f;
        _settings.MicrophoneMuted = (bool)(_btnMicMute.Tag ?? false);

        _settings.VirtualOutputVolume = _trkVirtualVolume.Value / 100.0f;
        _settings.VirtualOutputMuted = (bool)(_btnVirtualMute.Tag ?? false);

        _settings.PhysicalOutputVolume = _trkPhysicalVolume.Value / 100.0f;
        _settings.PhysicalOutputMuted = (bool)(_btnPhysicalMute.Tag ?? false);

        var selectedPlayback = _cboPlaybackDevice.SelectedItem as AudioDeviceInfo;
        _settings.SystemAudioDeviceId = (selectedPlayback == null || selectedPlayback.Id == "default") ? null : selectedPlayback.Id;

        _settings.AutoSwitchDefaultRecordingDevice = _chkAutoSwitchDefaultMic.Checked;
        var selectedVirtual = _cboVirtualPlaybackDevice.SelectedItem as AudioDeviceInfo;
        _settings.VirtualAudioPlaybackDeviceId = (selectedVirtual == null ||
                                                  selectedVirtual.Id.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                                                  selectedVirtual.Id.Equals("none", StringComparison.OrdinalIgnoreCase))
                                                  ? "off" : selectedVirtual.Id;

        var selectedPhysical = _cboPhysicalOutputDevice.SelectedItem as AudioDeviceInfo;
        _settings.PhysicalOutputDeviceId = (selectedPhysical == null ||
                                            selectedPhysical.Id.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                                            selectedPhysical.Id.Equals("none", StringComparison.OrdinalIgnoreCase))
                                            ? "off" : selectedPhysical.Id;

        _settings.AutoGrantMicrophonePermission = _chkAutoGrantMic.Checked;
        WebView2AudioPermissionTracker.AutoGrantMicrophone = _settings.AutoGrantMicrophonePermission;

        switch (_cboAudioSource.SelectedIndex)
        {
            case 0:
                _settings.AudioSource = AudioSourceMode.SystemAudioOnly;
                break;
            case 1:
                _settings.AudioSource = AudioSourceMode.MicrophoneOnly;
                break;
            case 2:
                _settings.AudioSource = AudioSourceMode.Both;
                break;
        }

        var selectedMic = _cboMicrophoneDevice.SelectedItem as AudioDeviceInfo;
        _settings.MicrophoneDeviceId = (selectedMic == null || selectedMic.Id == "default") ? null : selectedMic.Id;

        // Save Continuous Audio-to-Text Settings
        switch (_cboSpeechEngine.SelectedIndex)
        {
            case 0:
                _settings.SpeechToTextEngine = SpeechEngineFactory.EngineWindows;
                break;
            case 1:
                _settings.SpeechToTextEngine = SpeechEngineFactory.EngineOpenAI;
                break;
            case 2:
                _settings.SpeechToTextEngine = SpeechEngineFactory.EngineGroq;
                break;
            case 3:
                _settings.SpeechToTextEngine = SpeechEngineFactory.EngineCustom;
                break;
        }

        _settings.SpeechToTextApiKey = _txtSpeechApiKey.Text.Trim();
        _settings.SpeechToTextEndpoint = _txtSpeechEndpoint.Text.Trim();
        _settings.SpeechToTextModel = _txtSpeechModel.Text.Trim();
        _settings.SpeechToTextLanguage = _txtSpeechLanguage.Text.Trim();
        _settings.TranscriptionSilenceThreshold = (float)_numSilenceThreshold.Value;
        _settings.TranscriptionMaxChunkSeconds = (int)_numMaxChunkSeconds.Value;

        _overlay.ApplySettings(_settings);

        if (saveToDisk)
        {
            _settings.Save();
        }
    }
}
