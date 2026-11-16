using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MyOverlay;

public class BrowserTab : IDisposable
{
    public WebView2 WebView { get; }
    public string Title { get; private set; } = "New Tab";
    public string CurrentUrl { get; private set; } = "about:blank";
    public bool CanGoBack => WebView.CanGoBack;
    public bool CanGoForward => WebView.CanGoForward;
    public bool IsLoading { get; private set; }

    public event Action<BrowserTab>? StateChanged;
    public event Action<BrowserTab, string>? NewTabRequested;
    public event Action<Keys>? ShortcutTriggered;
    public event Action<BrowserTab, Point>? ViewportPointerDown;
    public event Action<BrowserTab>? EscapePressed;

    private CoreWebView2Controller? _controller;
    private bool _isDisposed;
    private bool _isInitializing;
    private bool _isInitialized;
    private string? _pendingUrl;

    public BrowserTab()
    {
        WebView = new WebView2
        {
            Dock = DockStyle.Fill,
            Visible = false
        };

        WebView.KeyDown += OnWebViewKeyDown;
    }

    public async Task InitializeAsync(CoreWebView2Environment env, string initialUrl)
    {
        if (_isDisposed || WebView.IsDisposed)
        {
            Debug.WriteLine("[MyOverlay] InitializeAsync skipped: control is already disposed.");
            return;
        }

        if (_isInitialized)
        {
            if (!string.IsNullOrWhiteSpace(initialUrl))
            {
                Navigate(initialUrl);
            }
            return;
        }

        if (_isInitializing)
        {
            if (!string.IsNullOrWhiteSpace(initialUrl))
            {
                _pendingUrl = initialUrl;
            }
            return;
        }

        _isInitializing = true;
        _pendingUrl = initialUrl;

        try
        {
            // Ensure the control's handle is created on the WinForms UI thread
            if (!WebView.IsHandleCreated)
            {
                WebView.CreateControl();
            }

            // Initialize WebView2 on the UI thread without ConfigureAwait(false)
            await WebView.EnsureCoreWebView2Async(env);

            _isInitialized = true;
            _isInitializing = false;

            // Register virtual host mapping for internal diagnostics (overlay.local -> WebAssets)
            try
            {
                string assetsDir = WebAssetsManager.GetAssetsDirectory();
                WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    WebAssetsManager.VirtualHostName,
                    assetsDir,
                    CoreWebView2HostResourceAccessKind.Allow
                );
            }
            catch (Exception mapEx)
            {
                Debug.WriteLine($"[MyOverlay] Could not map virtual host: {mapEx.Message}");
            }

            var settings = WebView.CoreWebView2.Settings;
            settings.IsScriptEnabled = true;
            settings.AreDefaultScriptDialogsEnabled = true;
            settings.IsWebMessageEnabled = true;
            settings.AreDevToolsEnabled = true;
            settings.AreDefaultContextMenusEnabled = true;
            settings.IsStatusBarEnabled = false;
            settings.IsZoomControlEnabled = true;

            // Hook navigation, permission and process events
            WebView.CoreWebView2.NavigationStarting += OnNavigationStarting;
            WebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            WebView.CoreWebView2.SourceChanged += OnSourceChanged;
            WebView.CoreWebView2.DocumentTitleChanged += OnDocumentTitleChanged;
            WebView.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
            WebView.CoreWebView2.PermissionRequested += OnPermissionRequested;
            WebView.CoreWebView2.ProcessFailed += OnProcessFailed;
            WebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            // Auto-redirect WebRTC getUserMedia to Virtual Audio Cable (CABLE Output)
            // This allows ChatGPT in WebView2 to receive the Virtual Bridge stream directly,
            // while Windows Default Recording Device remains the user's physical microphone for Google Meet!
            try
            {
                string routingScript = @"
                    (function() {
                        if (window.__myOverlayAudioInjected) return;
                        window.__myOverlayAudioInjected = true;
                        document.addEventListener('pointerdown', function(e) {
                            try { chrome.webview.postMessage('myOverlay:pointer:' + Math.round(e.clientX) + ':' + Math.round(e.clientY)); } catch (_) {}
                        }, true);
                        document.addEventListener('keydown', function(e) {
                            if (e.key === 'Escape') {
                                try { chrome.webview.postMessage('myOverlay:escape'); } catch (_) {}
                            }
                        }, true);
                        if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) return;
                        const originalGetUserMedia = navigator.mediaDevices.getUserMedia.bind(navigator.mediaDevices);
                        navigator.mediaDevices.getUserMedia = async function(constraints) {
                            try {
                                if (constraints && constraints.audio) {
                                    let hasForcedId = typeof constraints.audio === 'object' && constraints.audio.deviceId;
                                    if (!hasForcedId) {
                                        const devices = await navigator.mediaDevices.enumerateDevices();
                                        const virtualMic = devices.find(d => 
                                            d.kind === 'audioinput' && d.label && 
                                            (d.label.toLowerCase().includes('cable') || 
                                             d.label.toLowerCase().includes('vb-audio') || 
                                             d.label.toLowerCase().includes('virtual') ||
                                             d.label.toLowerCase().includes('voicemeeter'))
                                        );
                                        if (virtualMic && virtualMic.deviceId) {
                                            if (typeof constraints.audio === 'boolean') {
                                                constraints.audio = { deviceId: { exact: virtualMic.deviceId } };
                                            } else if (typeof constraints.audio === 'object') {
                                                constraints.audio.deviceId = { exact: virtualMic.deviceId };
                                            }
                                        }
                                    }
                                }
                            } catch (e) {
                                console.warn('[MyOverlay] WebRTC virtual mic redirect notice:', e);
                            }
                            return originalGetUserMedia(constraints);
                        };
                    })();
                ";
                await WebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(routingScript);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MyOverlay] Could not register audio routing script: {ex.Message}");
            }

            // Hook accelerator keys on the underlying controller if accessible
            try
            {
                var field = typeof(WebView2).GetField("_coreWebView2Controller", BindingFlags.NonPublic | BindingFlags.Instance);
                _controller = field?.GetValue(WebView) as CoreWebView2Controller;
                if (_controller != null)
                {
                    _controller.AcceleratorKeyPressed += OnAcceleratorKeyPressed;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MyOverlay] Could not bind accelerator controller: {ex.Message}");
            }

            // Navigate to pending or initial URL
            string targetUrl = !string.IsNullOrWhiteSpace(_pendingUrl) ? _pendingUrl : initialUrl;
            _pendingUrl = null;

            if (!string.IsNullOrWhiteSpace(targetUrl))
            {
                Navigate(targetUrl);
            }
        }
        catch (Exception ex)
        {
            _isInitializing = false;
            Debug.WriteLine($"[MyOverlay] WebView2 tab initialization failed: {ex}");
            throw;
        }
    }

    public void Navigate(string url)
    {
        if (_isDisposed || WebView.IsDisposed) return;

        if (string.IsNullOrWhiteSpace(url)) return;

        url = url.Trim();

        if (url.Equals("overlay://mic-test", StringComparison.OrdinalIgnoreCase) ||
            url.Equals("about:mic-test", StringComparison.OrdinalIgnoreCase))
        {
            url = WebAssetsManager.MicTestUrl;
        }

        // Ensure valid URI scheme so CoreWebView2.Navigate does not throw ArgumentException
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("file://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("about:", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("edge://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        if (!_isInitialized || WebView.CoreWebView2 == null)
        {
            // Store pending URL if initialization hasn't finished yet
            _pendingUrl = url;
            return;
        }

        try
        {
            WebView.CoreWebView2.Navigate(url);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MyOverlay] Navigation to '{url}' failed: {ex}");
        }
    }

    public void GoBack()
    {
        if (_isInitialized && WebView.CanGoBack)
        {
            WebView.GoBack();
        }
    }

    public void GoForward()
    {
        if (_isInitialized && WebView.CanGoForward)
        {
            WebView.GoForward();
        }
    }

    public void Reload()
    {
        if (_isInitialized)
        {
            WebView.Reload();
        }
    }

    public void Stop()
    {
        if (_isInitialized)
        {
            WebView.Stop();
        }
    }

    public void OpenDevTools()
    {
        WebView.CoreWebView2?.OpenDevToolsWindow();
    }

    public async Task<string?> ExecuteScriptAsync(string script)
    {
        if (!_isInitialized || WebView.CoreWebView2 == null) return null;
        return await WebView.CoreWebView2.ExecuteScriptAsync(script);
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        IsLoading = true;
        CurrentUrl = e.Uri;
        StateChanged?.Invoke(this);
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string message;
        try { message = e.TryGetWebMessageAsString(); }
        catch { return; }

        if (message.Equals("myOverlay:escape", StringComparison.Ordinal))
        {
            EscapePressed?.Invoke(this);
            return;
        }

        const string prefix = "myOverlay:pointer:";
        if (!message.StartsWith(prefix, StringComparison.Ordinal)) return;
        string[] parts = message[prefix.Length..].Split(':');
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)) return;
        ViewportPointerDown?.Invoke(this, new Point(x, y));
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        IsLoading = false;
        if (WebView.CoreWebView2 != null)
        {
            CurrentUrl = WebView.CoreWebView2.Source;
            if (!string.IsNullOrWhiteSpace(WebView.CoreWebView2.DocumentTitle))
            {
                Title = WebView.CoreWebView2.DocumentTitle;
            }
        }
        StateChanged?.Invoke(this);
    }

    private void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        if (WebView.CoreWebView2 != null)
        {
            CurrentUrl = WebView.CoreWebView2.Source;
            StateChanged?.Invoke(this);
        }
    }

    private void OnDocumentTitleChanged(object? sender, object e)
    {
        if (WebView.CoreWebView2 != null)
        {
            string title = WebView.CoreWebView2.DocumentTitle;
            Title = string.IsNullOrWhiteSpace(title) ? "New Tab" : title;
            StateChanged?.Invoke(this);
        }
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            string? targetUri = e.Uri;
            if (!string.IsNullOrWhiteSpace(targetUri) && !targetUri.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
            {
                e.Handled = true;
                // Dispatch tab addition asynchronously so the current WebView's event completes cleanly
                // before tab switching and visibility changes occur.
                WebView.BeginInvoke(new Action(() =>
                {
                    NewTabRequested?.Invoke(this, targetUri);
                }));
            }
            else
            {
                // For window.open() without an immediate URI, allow default handling
                e.Handled = false;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BrowserTab] NewWindowRequested error: {ex.Message}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        Debug.WriteLine($"[BrowserTab] WebView2 ProcessFailed: {e.ProcessFailedKind}, reason: {e.Reason}");
        if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited ||
            e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
        {
            try
            {
                WebView.BeginInvoke(new Action(() =>
                {
                    if (!WebView.IsDisposed)
                    {
                        WebView.Reload();
                    }
                }));
            }
            catch { }
        }
    }

    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        if (e.PermissionKind == CoreWebView2PermissionKind.Microphone)
        {
            if (WebView2AudioPermissionTracker.AutoGrantMicrophone)
            {
                e.State = CoreWebView2PermissionState.Allow;
                e.Handled = true;
            }
            else
            {
                e.State = CoreWebView2PermissionState.Default;
            }
            WebView2AudioPermissionTracker.RecordRequest(e.Uri, e.PermissionKind, e.State);
        }
        else
        {
            WebView2AudioPermissionTracker.RecordRequest(e.Uri, e.PermissionKind, e.State);
        }
    }

    private void OnAcceleratorKeyPressed(object? sender, CoreWebView2AcceleratorKeyPressedEventArgs e)
    {
        if (e.KeyEventKind != CoreWebView2KeyEventKind.KeyDown &&
            e.KeyEventKind != CoreWebView2KeyEventKind.SystemKeyDown)
        {
            return;
        }

        bool ctrl = (Control.ModifierKeys & Keys.Control) != 0;
        bool shift = (Control.ModifierKeys & Keys.Shift) != 0;
        bool alt = (Control.ModifierKeys & Keys.Alt) != 0;
        Keys key = (Keys)e.VirtualKey;

        if (ctrl && !alt && !shift && key == Keys.T)
        {
            e.Handled = true;
            ShortcutTriggered?.Invoke(Keys.Control | Keys.T);
        }
        else if (ctrl && !alt && !shift && key == Keys.W)
        {
            e.Handled = true;
            ShortcutTriggered?.Invoke(Keys.Control | Keys.W);
        }
        else if (ctrl && !alt && !shift && key == Keys.L)
        {
            e.Handled = true;
            ShortcutTriggered?.Invoke(Keys.Control | Keys.L);
        }
        else if ((ctrl && !alt && !shift && key == Keys.R) || (!ctrl && !alt && !shift && key == Keys.F5))
        {
            e.Handled = true;
            ShortcutTriggered?.Invoke(Keys.Control | Keys.R);
        }
        else if (ctrl && !alt && !shift && key == Keys.Tab)
        {
            e.Handled = true;
            ShortcutTriggered?.Invoke(Keys.Control | Keys.Tab);
        }
        else if (ctrl && !alt && shift && key == Keys.Tab)
        {
            e.Handled = true;
            ShortcutTriggered?.Invoke(Keys.Control | Keys.Shift | Keys.Tab);
        }
        else if (ctrl && !alt && !shift && key == Keys.B)
        {
            e.Handled = true;
            ShortcutTriggered?.Invoke(Keys.Control | Keys.B);
        }
        else if (!ctrl && !alt && !shift && key == Keys.F1)
        {
            e.Handled = true;
            ShortcutTriggered?.Invoke(Keys.F1);
        }
        else if (!ctrl && !alt && !shift && key == Keys.F12)
        {
            e.Handled = true;
            OpenDevTools();
        }
        else if (ctrl && !alt && !shift && key == Keys.F)
        {
            e.Handled = true;
            ShortcutTriggered?.Invoke(Keys.Control | Keys.F);
        }
        else if (ctrl && alt)
        {
            if (key == Keys.C) ShortcutTriggered?.Invoke(Keys.Control | Keys.Alt | Keys.C);
            else if (key == Keys.S) ShortcutTriggered?.Invoke(Keys.Control | Keys.Alt | Keys.S);
            else if (key == Keys.H) ShortcutTriggered?.Invoke(Keys.Control | Keys.Alt | Keys.H);
        }
    }

    private void OnWebViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && !e.Alt && !e.Shift && e.KeyCode == Keys.T)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            ShortcutTriggered?.Invoke(Keys.Control | Keys.T);
        }
        else if (e.Control && !e.Alt && !e.Shift && e.KeyCode == Keys.W)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            ShortcutTriggered?.Invoke(Keys.Control | Keys.W);
        }
        else if (e.Control && !e.Alt && !e.Shift && e.KeyCode == Keys.L)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            ShortcutTriggered?.Invoke(Keys.Control | Keys.L);
        }
        else if ((e.Control && !e.Alt && !e.Shift && e.KeyCode == Keys.R) || (!e.Control && !e.Alt && !e.Shift && e.KeyCode == Keys.F5))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            ShortcutTriggered?.Invoke(Keys.Control | Keys.R);
        }
        else if (e.Control && !e.Alt && !e.Shift && e.KeyCode == Keys.Tab)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            ShortcutTriggered?.Invoke(Keys.Control | Keys.Tab);
        }
        else if (e.Control && !e.Alt && e.Shift && e.KeyCode == Keys.Tab)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            ShortcutTriggered?.Invoke(Keys.Control | Keys.Shift | Keys.Tab);
        }
        else if (e.Control && !e.Alt && !e.Shift && e.KeyCode == Keys.B)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            ShortcutTriggered?.Invoke(Keys.Control | Keys.B);
        }
        else if (!e.Control && !e.Alt && !e.Shift && e.KeyCode == Keys.F1)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            ShortcutTriggered?.Invoke(Keys.F1);
        }
        else if (!e.Control && !e.Alt && !e.Shift && e.KeyCode == Keys.F12)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            OpenDevTools();
        }
        else if (e.Control && !e.Alt && !e.Shift && e.KeyCode == Keys.F)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            ShortcutTriggered?.Invoke(Keys.Control | Keys.F);
        }
        else if (e.Control && e.Alt)
        {
            if (e.KeyCode == Keys.C) ShortcutTriggered?.Invoke(Keys.Control | Keys.Alt | Keys.C);
            else if (e.KeyCode == Keys.S) ShortcutTriggered?.Invoke(Keys.Control | Keys.Alt | Keys.S);
            else if (e.KeyCode == Keys.H) ShortcutTriggered?.Invoke(Keys.Control | Keys.Alt | Keys.H);
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (_controller != null)
        {
            try
            {
                _controller.AcceleratorKeyPressed -= OnAcceleratorKeyPressed;
            }
            catch { }
            _controller = null;
        }

        if (WebView.CoreWebView2 != null)
        {
            try
            {
                WebView.CoreWebView2.NavigationStarting -= OnNavigationStarting;
                WebView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
                WebView.CoreWebView2.SourceChanged -= OnSourceChanged;
                WebView.CoreWebView2.DocumentTitleChanged -= OnDocumentTitleChanged;
                WebView.CoreWebView2.NewWindowRequested -= OnNewWindowRequested;
                WebView.CoreWebView2.PermissionRequested -= OnPermissionRequested;
                WebView.CoreWebView2.ProcessFailed -= OnProcessFailed;
                WebView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
            }
            catch { }
        }

        WebView.KeyDown -= OnWebViewKeyDown;
        WebView.Dispose();
    }
}
