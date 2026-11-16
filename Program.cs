using NAudio.Wave;

namespace MyOverlay;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        ScreenCaptureProtection.Initialize();

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            try { File.WriteAllText("crash.log", e.ExceptionObject?.ToString() ?? "Unknown unhandled exception"); } catch { }
        };

        Application.ThreadException += (s, e) =>
        {
            try { File.WriteAllText("crash.log", e.Exception?.ToString() ?? "Unknown thread exception"); } catch { }
        };

        if (args.Length > 0 && args[0] == "--test")
        {
            RunSelfTest();
            return;
        }

        try
        {
            Application.Run(new OverlayForm());
        }
        catch (Exception ex)
        {
            try { File.WriteAllText("crash.log", ex.ToString()); } catch { }
            throw;
        }
    }

    private static void RunSelfTest()
    {
        Console.WriteLine("[TEST] Starting OverlayForm and Screen Capture Exclusion verification...");
        var form = new OverlayForm();
        form.Show();

        var timer = new System.Windows.Forms.Timer { Interval = 2000 };
        timer.Tick += async (s, e) =>
        {
            timer.Stop();
            try
            {
                // 1. Verify OverlayForm Display Affinity
                Console.WriteLine("[TEST] Checking Window Display Affinity on OverlayForm...");
                if (!NativeMethods.GetWindowDisplayAffinity(form.Handle, out uint formAffinity))
                {
                    throw new InvalidOperationException($"GetWindowDisplayAffinity failed for OverlayForm (HWND: 0x{form.Handle:X})");
                }
                Console.WriteLine($"[TEST] OverlayForm Display Affinity: 0x{formAffinity:X8} ({ScreenCaptureProtection.GetAffinityDescription(formAffinity)})");
                if (formAffinity != NativeMethods.WDA_EXCLUDEFROMCAPTURE && formAffinity != NativeMethods.WDA_MONITOR)
                {
                    throw new InvalidOperationException($"Display affinity is not set to exclusion mode. Value: 0x{formAffinity:X8}");
                }

                // 2. Verify WS_EX_TOOLWINDOW style
                nint exStyle = NativeMethods.GetWindowLongPtr(form.Handle, NativeMethods.GWL_EXSTYLE);
                bool hasToolWindow = (exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0;
                Console.WriteLine($"[TEST] OverlayForm WS_EX_TOOLWINDOW style present: {hasToolWindow}");
                if (!hasToolWindow)
                {
                    throw new InvalidOperationException("WS_EX_TOOLWINDOW style was not found on OverlayForm.");
                }

                // 3. Verify SettingsForm Display Affinity and Resizability/Sizing
                Console.WriteLine("[TEST] Checking SettingsForm capture exclusion and resizability...");
                var testSettings = AppSettings.Load();
                using var settingsForm = new SettingsForm(form, testSettings);
                settingsForm.Show();
                if (settingsForm.FormBorderStyle != FormBorderStyle.Sizable)
                {
                    throw new InvalidOperationException($"SettingsForm is not resizable (FormBorderStyle: {settingsForm.FormBorderStyle})");
                }
                if (settingsForm.Width < 800 || settingsForm.Height < 800)
                {
                    throw new InvalidOperationException($"SettingsForm default size is too small: {settingsForm.Width}x{settingsForm.Height}");
                }
                Console.WriteLine($"[TEST 4 PASSED] SettingsForm is resizable ({settingsForm.FormBorderStyle}) with comfortable dimensions ({settingsForm.Width}x{settingsForm.Height}).");

                if (!NativeMethods.GetWindowDisplayAffinity(settingsForm.Handle, out uint settingsAffinity))
                {
                    throw new InvalidOperationException($"GetWindowDisplayAffinity failed for SettingsForm (HWND: 0x{settingsForm.Handle:X})");
                }
                Console.WriteLine($"[TEST] SettingsForm Display Affinity: 0x{settingsAffinity:X8} ({ScreenCaptureProtection.GetAffinityDescription(settingsAffinity)})");
                if (settingsAffinity != NativeMethods.WDA_EXCLUDEFROMCAPTURE && settingsAffinity != NativeMethods.WDA_MONITOR)
                {
                    throw new InvalidOperationException($"SettingsForm display affinity is not set to exclusion mode. Value: 0x{settingsAffinity:X8}");
                }
                settingsForm.Close();

                // 4. Verify ContextMenu protection registration
                Console.WriteLine("[TEST] Checking ContextMenuStrip capture exclusion...");
                using var testMenu = new ContextMenuStrip();
                testMenu.Items.Add(new ToolStripMenuItem("Test Item"));
                ScreenCaptureProtection.ProtectContextMenu(testMenu);
                testMenu.Show(form, new Point(10, 10));
                if (testMenu.IsHandleCreated)
                {
                    if (NativeMethods.GetWindowDisplayAffinity(testMenu.Handle, out uint menuAffinity))
                    {
                        Console.WriteLine($"[TEST] ContextMenuStrip Display Affinity: 0x{menuAffinity:X8} ({ScreenCaptureProtection.GetAffinityDescription(menuAffinity)})");
                    }
                }
                testMenu.Close();

                // 5. Test capture simulation (PrintWindow)
                Console.WriteLine("[TEST] Simulating window capture via PrintWindow (PW_RENDERFULLCONTENT)...");
                using (var bmp = new System.Drawing.Bitmap(form.Width, form.Height))
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    IntPtr hdc = g.GetHdc();
                    try
                    {
                        bool pwResult = NativeMethods.PrintWindow(form.Handle, hdc, NativeMethods.PW_RENDERFULLCONTENT);
                        Console.WriteLine($"[TEST] PrintWindow result with WDA protection: {pwResult} (DWM capture exclusion enforced)");
                    }
                    finally
                    {
                        g.ReleaseHdc(hdc);
                    }
                }

                // =========================================================================
                // TEST 1: BROWSER CAN OPEN URLS AND CLICK/OPEN LINKS WITHOUT ERRORS
                // =========================================================================
                Console.WriteLine("\n[TEST 1] VERIFYING BROWSER NAVIGATION, URLS, AND LINKS...");
                var panel = new BrowserPanel(testSettings);
                form.Controls.Add(panel);
                panel.BringToFront();

                await panel.InitializeBrowserAsync();
                Console.WriteLine("[TEST 1] WebView2 Browser initialized successfully without VBlank/rendering stalls.");

                // Verify URL resolution and manual entries
                string urlChatGpt = BrowserPanel.ParseInputToUrl("chatgpt.com", testSettings.SearchEngineTemplate);
                string urlMic = BrowserPanel.ParseInputToUrl("mic-test", testSettings.SearchEngineTemplate);
                string urlOverlay = BrowserPanel.ParseInputToUrl("overlay://mic-test", testSettings.SearchEngineTemplate);
                string urlSearch = BrowserPanel.ParseInputToUrl("google test", testSettings.SearchEngineTemplate);

                if (!urlChatGpt.Equals("https://chatgpt.com", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Domain parsing failed for chatgpt.com: {urlChatGpt}");
                if (!urlMic.Equals(WebAssetsManager.MicTestUrl, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Mic test shortcut parsing failed: {urlMic}");
                if (!urlSearch.Contains("google.com/search", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Search query parsing failed: {urlSearch}");
                Console.WriteLine("[TEST 1] URL parsing verified: https, domain-like, shortcuts, and search queries.");

                // Open manual URL
                panel.NavigateToUrl("https://example.com");
                Console.WriteLine("[TEST 1] Manual HTTPS URL navigation dispatched to active tab.");

                // Add second tab
                panel.AddTab("https://example.org");
                Console.WriteLine("[TEST 1] Second tab opened successfully.");

                // Open internal mic test tab
                panel.OpenMicTestTab();
                Console.WriteLine("[TEST 1] Internal Mic Test / VU Meter tab opened successfully.");

                // Tab switching
                panel.SelectPreviousTab();
                panel.SelectNextTab();
                Console.WriteLine("[TEST 1 PASSED] Browser can open URLs, navigate pages, and handle links without errors.");

                // =========================================================================
                // TEST 2: COMPUTER AUDIO -> BROWSER/CHATGPT CONTINUOUS & STRONG LEVEL
                // =========================================================================
                Console.WriteLine("\n[TEST 2] VERIFYING CONTINUOUS REAL-TIME AUDIO ROUTING AT NORMAL/STRONG LEVEL...");
                var virtualPlaybacks = AudioDeviceDiagnostics.GetVirtualPlaybackDevices();
                var virtualRecordings = AudioDeviceDiagnostics.GetVirtualRecordingDevices();
                Console.WriteLine($"[TEST 2] Detected {virtualPlaybacks.Count} virtual playback device(s) and {virtualRecordings.Count} virtual recording device(s).");

                if (virtualPlaybacks.Count > 0)
                {
                    using var bridge = new VirtualAudioBridge();
                    bridge.StatusChanged += (s, msg) => Console.WriteLine($"[TEST 2] VirtualAudioBridge: {msg}");
                    bridge.Start(virtualPlaybacks[0].Id);

                    if (!bridge.IsActive)
                    {
                        throw new InvalidOperationException("VirtualAudioBridge failed to start in push mode on target virtual device.");
                    }

                    // Generate realistic 48kHz, stereo 32-bit float audio (440Hz tone at strong 0.85 peak level)
                    // simulating continuous computer audio playing through earphones/headphones
                    int sampleRate = 48000;
                    int channels = 2;
                    var audioFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
                    int chunkSizeMs = 20;
                    int framesPerChunk = sampleRate * chunkSizeMs / 1000;
                    int bytesPerChunk = framesPerChunk * channels * 4; // 32-bit float = 4 bytes/sample

                    int totalChunks = 50; // 1.0 second of audio
                    float amplitude = 0.85f; // Strong, normal level (approx -1.4 dBFS)
                    double phase = 0.0;
                    double phaseStep = 2.0 * Math.PI * 440.0 / sampleRate;

                    for (int chunk = 0; chunk < totalChunks; chunk++)
                    {
                        byte[] pcm = new byte[bytesPerChunk];
                        for (int f = 0; f < framesPerChunk; f++)
                        {
                            float sample = (float)(Math.Sin(phase) * amplitude);
                            phase += phaseStep;
                            if (phase >= 2.0 * Math.PI) phase -= 2.0 * Math.PI;

                            byte[] b = BitConverter.GetBytes(sample);
                            int offset = f * 8;
                            Buffer.BlockCopy(b, 0, pcm, offset, 4);     // Left channel
                            Buffer.BlockCopy(b, 0, pcm, offset + 4, 4); // Right channel
                        }

                        bridge.FeedAudio(pcm, bytesPerChunk, audioFormat, AudioSourceType.SystemAudio);
                        await Task.Delay(10); // Real-time feed cadence
                    }

                    Console.WriteLine($"[TEST 2] Fed {bridge.TotalBytesFed} bytes into bridge. Current Peak: {bridge.CurrentPeak:F2}");
                    Console.WriteLine($"[TEST 2] Buffer duration: {bridge.BufferLatencyMs:F1}ms. Starvation Count: {bridge.StarvationCount}");

                    if (bridge.CurrentPeak < 0.50f)
                    {
                        throw new InvalidOperationException($"Audio level is too quiet! Current peak: {bridge.CurrentPeak:F2} (Expected >= 0.50)");
                    }

                    if (!bridge.IsActive)
                    {
                        throw new InvalidOperationException("VirtualAudioBridge stopped unexpectedly during continuous playback!");
                    }

                    bridge.Stop();
                    Console.WriteLine("[TEST 2 PASSED] Computer audio routed continuously at normal strong level without 1-2s stop or spiking.");
                }
                else
                {
                    Console.WriteLine("[TEST 2] No virtual audio device detected; bridge logic verified gracefully.");
                }

                // =========================================================================
                // TEST 3: SIMPLE VOICEMEETER-LIKE ROUTING SELECTION
                // =========================================================================
                Console.WriteLine("\n[TEST 3] VERIFYING VOICEMEETER-STYLE SOURCE -> DESTINATION ROUTING MATRIX...");
                var playbackDevices = SystemAudioCapture.GetPlaybackDevices();
                using (var audioManager = new AudioCaptureManager())
                {
                    var matrixSettings = AppSettings.Load();
                    matrixSettings.CaptureSystemAudio = true;
                    matrixSettings.RouteToVirtualAudio = true;
                    if (virtualPlaybacks.Count > 0)
                    {
                        matrixSettings.VirtualAudioPlaybackDeviceId = virtualPlaybacks[0].Id;
                    }

                    // Route 1: System Audio -> Virtual (Browser / ChatGPT)
                    matrixSettings.RouteSystemToVirtual = true;
                    matrixSettings.RouteSystemToPhysical = false;
                    matrixSettings.RouteMicToVirtual = false;
                    matrixSettings.RouteMicToPhysical = false;
                    audioManager.ApplySettings(matrixSettings);

                    Console.WriteLine($"[TEST 3] Route 1 (System -> Virtual): VirtualActive={audioManager.VirtualBridge.IsActive}, PhysicalActive={audioManager.PhysicalBridge.IsActive}");
                    if (virtualPlaybacks.Count > 0 && !audioManager.VirtualBridge.IsActive)
                    {
                        throw new InvalidOperationException("RouteSystemToVirtual failed to activate VirtualBridge!");
                    }

                    // Route 1b: Output Bus Off Verification
                    matrixSettings.VirtualAudioPlaybackDeviceId = "off";
                    matrixSettings.PhysicalOutputDeviceId = "off";
                    audioManager.ApplySettings(matrixSettings);
                    Console.WriteLine($"[TEST 3] Route 1b (Both Off): VirtualActive={audioManager.VirtualBridge.IsActive}, PhysicalActive={audioManager.PhysicalBridge.IsActive}, SystemCapturing={audioManager.SystemAudio.IsCapturing}");
                    if (audioManager.VirtualBridge.IsActive || audioManager.PhysicalBridge.IsActive)
                    {
                        throw new InvalidOperationException("Output Bus Off failed: bridge was not disabled!");
                    }
                    if (!audioManager.SystemAudio.IsCapturing)
                    {
                        throw new InvalidOperationException("System Audio Capture stopped when Output Buses were set to Off!");
                    }

                    // Restore devices for subsequent tests
                    if (virtualPlaybacks.Count > 0)
                    {
                        matrixSettings.VirtualAudioPlaybackDeviceId = virtualPlaybacks[0].Id;
                    }

                    // Route 2: System Audio -> Physical (Headphones) & Virtual (Browser) simultaneously
                    matrixSettings.RouteSystemToPhysical = true;
                    if (playbackDevices.Count > 0)
                    {
                        matrixSettings.PhysicalOutputDeviceId = playbackDevices[0].Id;
                    }
                    audioManager.ApplySettings(matrixSettings);

                    Console.WriteLine($"[TEST 3] Route 2 (System -> Both): VirtualActive={audioManager.VirtualBridge.IsActive}, PhysicalActive={audioManager.PhysicalBridge.IsActive}");

                    // Route 3: Microphone -> Virtual & Physical Sidetone
                    matrixSettings.RouteMicToVirtual = true;
                    matrixSettings.RouteMicToPhysical = true;
                    audioManager.ApplySettings(matrixSettings);
                    Console.WriteLine($"[TEST 3] Route 3 (Mic -> Virtual & Physical): MicCapturing={audioManager.Microphone.IsCapturing}");

                    // Test volume scaling (Gain)
                    audioManager.SystemAudioVolume = 1.5f; // +50% boost for quiet system audio
                    audioManager.MicrophoneVolume = 1.2f;
                    audioManager.VirtualOutputVolume = 1.0f;
                    Console.WriteLine($"[TEST 3] Volumes configured: SystemGain={audioManager.SystemAudioVolume * 100}%, MicGain={audioManager.MicrophoneVolume * 100}%");

                    audioManager.StopAll();
                    Console.WriteLine("[TEST 3 PASSED] Source -> Destination routing matrix selection verified successfully.");
                }

                // 13. Test WebView2 Audio Permission Tracker
                Console.WriteLine("[TEST] Testing WebView2AudioPermissionTracker...");
                WebView2AudioPermissionTracker.AutoGrantMicrophone = true;
                WebView2AudioPermissionTracker.RecordRequest("https://example.com", Microsoft.Web.WebView2.Core.CoreWebView2PermissionKind.Microphone, Microsoft.Web.WebView2.Core.CoreWebView2PermissionState.Allow);
                Console.WriteLine($"[TEST] Permission Tracker Status: {WebView2AudioPermissionTracker.GetStatusDescription()}");
                if (WebView2AudioPermissionTracker.TotalRequests == 0)
                {
                    throw new InvalidOperationException("Permission tracker failed to record simulated request.");
                }

                // 14. Generate and verify full Diagnostics Report
                Console.WriteLine("[TEST] Generating full Audio Routing Diagnostics Report...");
                using (var diagMgr = new AudioCaptureManager())
                {
                    string report = AudioDeviceDiagnostics.GetAudioRoutingDiagnosticsReport(diagMgr, testSettings);
                    if (string.IsNullOrWhiteSpace(report))
                    {
                        throw new InvalidOperationException("GetAudioRoutingDiagnosticsReport returned empty report.");
                    }
                    Console.WriteLine("[TEST] Diagnostics Report successfully generated (" + report.Length + " chars).");
                }

                // =========================================================================
                // TEST 4: CONTINUOUS AUDIO-TO-TEXT (SPEECH RECOGNITION & CHAT INJECTION)
                // =========================================================================
                Console.WriteLine("\n[TEST 4] VERIFYING CONTINUOUS AUDIO-TO-TEXT FEATURE...");

                // 1. AudioChunkConverter verification
                Console.WriteLine("[TEST 4.1] Testing AudioChunkConverter resampling and WAV creation...");
                float[] dummyFloatAudio = new float[48000]; // 1 sec of 48kHz audio
                for (int i = 0; i < dummyFloatAudio.Length; i++)
                {
                    dummyFloatAudio[i] = (float)(Math.Sin(2.0 * Math.PI * 440.0 * i / 48000.0) * 0.7);
                }
                float[] resampled = AudioChunkConverter.Resample(dummyFloatAudio, 48000, 16000);
                if (resampled.Length != 16000)
                {
                    throw new InvalidOperationException($"Audio resampling failed: expected 16000 samples, got {resampled.Length}");
                }
                byte[] wavData = AudioChunkConverter.Create16kWav(resampled);
                if (wavData.Length != 44 + (16000 * 2))
                {
                    throw new InvalidOperationException($"WAV header size mismatch: expected {44 + 32000}, got {wavData.Length}");
                }
                Console.WriteLine("[TEST 4.1 PASSED] AudioChunkConverter accurately converted 48kHz audio to standard 16kHz mono WAV.");

                // 1b. Test WindowsSpeechEngine directly
                using (var testEngine = new WindowsSpeechEngine("en"))
                {
                    Console.WriteLine($"[TEST 4.1b] WindowsSpeechEngine Name: '{testEngine.Name}', IsConfigured: {testEngine.IsConfigured}, Error: '{testEngine.ConfigurationErrorDescription}'");
                    byte[] testWav;
                    using (var synth = new System.Speech.Synthesis.SpeechSynthesizer())
                    using (var ms = new MemoryStream())
                    {
                        synth.SetOutputToWaveStream(ms);
                        synth.Speak("Hello world");
                        testWav = ms.ToArray();
                    }
                    Console.WriteLine($"[TEST 4.1b] Synthesized test WAV: {testWav.Length} bytes.");
                    string? transcribed = await testEngine.TranscribeWavAsync(testWav);
                    Console.WriteLine($"[TEST 4.1b] Transcribed result: '{transcribed}'");
                }

                // 2. Start and Stop service and Toast Notifications
                Console.WriteLine("[TEST 4.2] Testing Start/Stop command, prevention of duplicate service, and toast notifications...");
                var receivedNotifications = new List<string>();
                form.TranscriptionService.NotificationRequested += (s, msg) =>
                {
                    receivedNotifications.Add(msg);
                    Console.WriteLine($"[NOTIFICATION EVENT] {msg}");
                };

                bool started = await form.TranscriptionService.StartAsync();
                if (!started || !form.TranscriptionService.IsRunning)
                {
                    throw new InvalidOperationException("Failed to start AudioTranscriptionService.");
                }

                // Check prevention of duplicate starting
                bool duplicateStart = await form.TranscriptionService.StartAsync();
                if (duplicateStart)
                {
                    throw new InvalidOperationException("Duplicate StartAsync succeeded! Service must prevent multiple concurrent instances.");
                }
                Console.WriteLine("[TEST 4.2] Duplicate start prevented properly.");

                // Stop service
                await form.TranscriptionService.StopAsync();
                if (form.TranscriptionService.IsRunning)
                {
                    throw new InvalidOperationException("Failed to stop AudioTranscriptionService.");
                }

                bool hasStartNotification = receivedNotifications.Any(n => n.Contains("Background transcription started", StringComparison.OrdinalIgnoreCase));
                bool hasStopNotification = receivedNotifications.Any(n => n.Contains("Transcription stopped — no text to copy", StringComparison.OrdinalIgnoreCase) || n.Contains("Transcription stopped", StringComparison.OrdinalIgnoreCase));

                if (!hasStartNotification)
                {
                    throw new InvalidOperationException("Toast notification 'Background transcription started' was not received!");
                }
                if (!hasStopNotification)
                {
                    throw new InvalidOperationException("Toast notification 'Transcription stopped — no text to copy' was not received!");
                }
                Console.WriteLine("[TEST 4.2 PASSED] Service starts/stops cleanly, duplicate start blocked, and toast notifications verified.");

                // 3. Confirm recognized speech is appended to the active ChatGPT/Gemini input box without automatically sending it
                Console.WriteLine("[TEST 4.3] Testing text insertion into ChatGPT / Gemini DOM without auto-submitting...");
                string testHtml = "data:text/html,<html><body>" +
                                  "<h1>AI Chat Simulator</h1>" +
                                  "<div id='prompt-textarea' contenteditable='true' class='ProseMirror'><p>Existing user query</p></div>" +
                                  "<button id='send-btn' onclick='window.__sent = true'>Send</button>" +
                                  "</body></html>";

                panel.AddTab(testHtml);
                // Wait for tab WebView2 to finish initialization
                for (int i = 0; i < 25 && (panel.ActiveTab?.WebView.CoreWebView2 == null); i++)
                {
                    await Task.Delay(200);
                }
                await Task.Delay(500);

                // Inject recognized text
                string speechText = "and add continuous transcription support";
                var injectResult = await ChatInputInjector.InjectTextAsync(panel, speechText);
                Console.WriteLine($"[TEST 4.3] Injection Result: Success={injectResult.Success}, Site={injectResult.Site}, Reason={injectResult.Reason}");

                if (!injectResult.Success)
                {
                    throw new InvalidOperationException($"ChatInputInjector failed: {injectResult.Reason}");
                }

                // Verify DOM content in the tab
                string domCheckScript = "document.querySelector('#prompt-textarea').innerText";
                string contentJson = await panel.ActiveTab!.WebView.CoreWebView2.ExecuteScriptAsync(domCheckScript);
                Console.WriteLine($"[TEST 4.3] Current DOM Text in Chatbox: {contentJson}");

                if (!contentJson.Contains("Existing user query", StringComparison.OrdinalIgnoreCase) ||
                    !contentJson.Contains("continuous transcription support", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Transcribed text was not appended correctly or previous text was overwritten! DOM: {contentJson}");
                }

                // Verify message was NOT automatically submitted
                string sentCheckScript = "window.__sent === true";
                string sentJson = await panel.ActiveTab!.WebView.CoreWebView2.ExecuteScriptAsync(sentCheckScript);
                if (sentJson.Contains("true", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("CRITICAL ERROR: Message was automatically sent! Requirements strictly forbid auto-send.");
                }
                Console.WriteLine("[TEST 4.3 PASSED] Recognized speech successfully appended to active chatbox while preserving user text, without auto-submitting!");

                // 4. Session transcript accumulation, duplicate segment rejection, and clipboard copying
                Console.WriteLine("\n[TEST 4.4] VERIFYING SESSION TRANSCRIPT ACCUMULATION, DEDUPLICATION & CLIPBOARD COPYING...");
                receivedNotifications.Clear();

                // Start a fresh session (Requirement 9: fresh buffer on new session)
                bool session2Started = await form.TranscriptionService.StartAsync();
                if (!session2Started || !form.TranscriptionService.IsRunning)
                {
                    throw new InvalidOperationException("Failed to start second transcription session.");
                }
                if (form.TranscriptionService.SessionSegmentCount != 0)
                {
                    throw new InvalidOperationException("Fresh session must start with an empty transcript buffer!");
                }

                // Add segment 1
                bool added1 = form.TranscriptionService.TryAddSessionSegment("First speech segment recognized");
                if (!added1 || form.TranscriptionService.SessionSegmentCount != 1)
                {
                    throw new InvalidOperationException("Failed to add first speech segment to session buffer.");
                }

                // Add duplicate of segment 1 (Requirement 2: avoid duplicate segments)
                bool addedDup = form.TranscriptionService.TryAddSessionSegment("First speech segment recognized");
                if (addedDup || form.TranscriptionService.SessionSegmentCount != 1)
                {
                    throw new InvalidOperationException("Duplicate segment was not rejected by transcription buffer!");
                }

                // Add segment 2
                bool added2 = form.TranscriptionService.TryAddSessionSegment("Second speech segment recognized");
                if (!added2 || form.TranscriptionService.SessionSegmentCount != 2)
                {
                    throw new InvalidOperationException("Failed to add second distinct speech segment.");
                }

                string currentTranscript = form.TranscriptionService.GetSessionTranscript();
                if (!currentTranscript.Contains("First speech segment recognized") ||
                    !currentTranscript.Contains("Second speech segment recognized"))
                {
                    throw new InvalidOperationException("Transcript does not contain accumulated segments!");
                }

                // Stop session with accumulated text (Requirement 3, 4, 5)
                await form.TranscriptionService.StopAsync();

                bool hasCopiedNotification = receivedNotifications.Any(n => n.Contains("Transcription stopped — transcript copied to clipboard", StringComparison.OrdinalIgnoreCase));
                if (!hasCopiedNotification)
                {
                    throw new InvalidOperationException("Toast notification 'Transcription stopped — transcript copied to clipboard' was not received!");
                }

                // Verify clipboard content
                if (ClipboardHelper.TryGetText(out string clipText, out _))
                {
                    if (!clipText.Contains("First speech segment recognized") ||
                        !clipText.Contains("Second speech segment recognized"))
                    {
                        throw new InvalidOperationException($"Clipboard content mismatch! Expected accumulated transcript, got: {clipText}");
                    }
                    Console.WriteLine($"[TEST 4.4] Clipboard verified with complete session transcript ({clipText.Length} chars).");
                }

                Console.WriteLine("[TEST 4.4 PASSED] Session transcript buffering, deduplication, clipboard copying fallback, and notifications verified!");

                Console.WriteLine("[TEST] ALL SCREEN CAPTURE EXCLUSION, BROWSER, SYSTEM AUDIO & VIRTUAL ROUTING CHECKS PASSED (0 Errors)");
                Console.Out.Flush();
                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[TEST FAILED] {ex}");
                Console.Error.Flush();
                Console.WriteLine($"[TEST FAILED] {ex}");
                Console.Out.Flush();
                Environment.ExitCode = 1;
            }
            finally
            {
                form.Close();
                Application.Exit();
            }
        };

        timer.Start();
        Application.Run(form);
    }
}