using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace MyOverlay;

public class TranscriptionEventArgs : EventArgs
{
    public string Text { get; }
    public bool IsFinal { get; }
    public string? Site { get; }

    public TranscriptionEventArgs(string text, bool isFinal = true, string? site = null)
    {
        Text = text;
        IsFinal = isFinal;
        Site = site;
    }
}

/// <summary>
/// Continuous Background Audio-to-Text coordinator.
/// Listens to system audio loopback capture, chunks speech based on voice activity and silence,
/// transcribes via ISpeechToTextEngine, and automatically inserts text into the active ChatGPT or Gemini chat input.
/// </summary>
public sealed class AudioTranscriptionService : IDisposable
{
    private readonly object _syncLock = new();
    private readonly AudioCaptureManager _audioManager;
    private readonly Func<BrowserPanel?> _browserPanelProvider;
    private AppSettings _settings;
    private ISpeechToTextEngine? _engine;

    private bool _isRunning;
    private bool _isDisposed;
    private bool _startedSystemAudioExclusively;
    private bool _startedMicCaptureExclusively;

    public float CurrentPeakLevel { get; private set; }
    public event EventHandler<float>? AudioLevelChanged;

    // Audio buffering & VAD
    private readonly List<float> _currentChunkSamples = new();
    private readonly Queue<float> _preRollBuffer = new();
    private const int PreRollSampleCapacity = 4800; // ~300ms at 16kHz
    private bool _isSpeechDetected;
    private int _consecutiveSilentFrames;
    private int _consecutiveSpeechFrames;

    // Thread-safe Session Transcription Buffer
    private readonly List<string> _sessionSegments = new();
    private string? _lastAppendedSegment;

    // Transcription worker channel
    private Channel<byte[]>? _audioQueue;
    private CancellationTokenSource? _workerCts;
    private Task? _workerTask;

    public bool IsRunning
    {
        get
        {
            lock (_syncLock) return _isRunning;
        }
    }

    public string? LastRecognizedText { get; private set; }
    public string CurrentEngineName => _engine?.Name ?? "None";

    public IReadOnlyList<string> SessionSegments
    {
        get
        {
            lock (_syncLock) return _sessionSegments.ToArray();
        }
    }

    public int SessionSegmentCount
    {
        get
        {
            lock (_syncLock) return _sessionSegments.Count;
        }
    }

    public int SessionCharacterCount
    {
        get
        {
            lock (_syncLock) return _sessionSegments.Sum(s => s.Length);
        }
    }

    public string GetSessionTranscript()
    {
        lock (_syncLock)
        {
            if (_sessionSegments.Count == 0) return string.Empty;
            return string.Join(Environment.NewLine, _sessionSegments);
        }
    }

    /// <summary>
    /// Thread-safely records a finalized, non-empty speech-to-text segment into the session buffer.
    /// Avoids duplicate transcript segments against the immediately preceding segment.
    /// </summary>
    /// <param name="text">Segment text to add.</param>
    /// <returns>True if segment was added; false if rejected (null/whitespace or duplicate).</returns>
    public bool TryAddSessionSegment(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string trimmed = text.Trim();

        lock (_syncLock)
        {
            if (!string.IsNullOrEmpty(_lastAppendedSegment) &&
                string.Equals(_lastAppendedSegment, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            _sessionSegments.Add(trimmed);
            _lastAppendedSegment = trimmed;
            LastRecognizedText = trimmed;
            return true;
        }
    }

    /// <summary>
    /// Clears the session transcript buffer and resets the last appended segment tracker.
    /// </summary>
    public void ClearSessionTranscript()
    {
        lock (_syncLock)
        {
            _sessionSegments.Clear();
            _lastAppendedSegment = null;
            LastRecognizedText = null;
        }
    }

    public event EventHandler<bool>? StateChanged;
    public event EventHandler<TranscriptionEventArgs>? TranscriptionReceived;
    public event EventHandler<string>? NotificationRequested;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<Exception>? ErrorOccurred;

    public AudioTranscriptionService(
        AudioCaptureManager audioManager,
        Func<BrowserPanel?> browserPanelProvider,
        AppSettings settings)
    {
        _audioManager = audioManager ?? throw new ArgumentNullException(nameof(audioManager));
        _browserPanelProvider = browserPanelProvider ?? throw new ArgumentNullException(nameof(browserPanelProvider));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        _engine = SpeechEngineFactory.CreateEngine(_settings);
    }

    /// <summary>
    /// Updates configuration and recreates speech-to-text engine if settings changed.
    /// </summary>
    public void UpdateSettings(AppSettings settings)
    {
        lock (_syncLock)
        {
            _settings = settings;
            if (!_isRunning)
            {
                _engine?.Dispose();
                _engine = SpeechEngineFactory.CreateEngine(_settings);
            }
        }
    }

    /// <summary>
    /// Starts continuous background audio transcription.
    /// </summary>
    public async Task<bool> StartAsync()
    {
        lock (_syncLock)
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(AudioTranscriptionService));
            if (_isRunning) return false; // Prevent multiple instances starting simultaneously
        }

        // 1. Verify Speech Engine is configured
        lock (_syncLock)
        {
            _engine?.Dispose();
            _engine = SpeechEngineFactory.CreateEngine(_settings);

            if (!_engine.IsConfigured)
            {
                string errorMsg = string.IsNullOrWhiteSpace(_engine.ConfigurationErrorDescription)
                    ? "Speech-to-Text engine is not configured."
                    : _engine.ConfigurationErrorDescription;

                NotificationRequested?.Invoke(this, $"Transcription cannot start: {errorMsg}");
                StatusChanged?.Invoke(this, $"[Audio-to-Text] Start blocked: {errorMsg}");
                return false;
            }
        }

        // 2. Start audio sources based on configured AudioSourceMode
        bool captureSystem = _settings.AudioSource == AudioSourceMode.SystemAudioOnly || _settings.AudioSource == AudioSourceMode.Both;
        bool captureMic = _settings.AudioSource == AudioSourceMode.MicrophoneOnly || _settings.AudioSource == AudioSourceMode.Both;

        if (captureSystem && !_audioManager.SystemAudio.IsCapturing)
        {
            try
            {
                await _audioManager.SystemAudio.StartAsync();
                _startedSystemAudioExclusively = true;
            }
            catch (Exception ex)
            {
                NotificationRequested?.Invoke(this, $"Failed to start system audio capture: {ex.Message}");
                ErrorOccurred?.Invoke(this, ex);
                return false;
            }
        }
        else
        {
            _startedSystemAudioExclusively = false;
        }

        if (captureMic && !_audioManager.Microphone.IsCapturing)
        {
            try
            {
                await _audioManager.Microphone.StartAsync();
                _startedMicCaptureExclusively = true;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke(this, $"[Audio-to-Text] Note: Microphone capture start error: {ex.Message}");
            }
        }
        else
        {
            _startedMicCaptureExclusively = false;
        }

        // 3. Initialize worker channel and queues
        lock (_syncLock)
        {
            _sessionSegments.Clear();
            _lastAppendedSegment = null;
            _currentChunkSamples.Clear();
            _preRollBuffer.Clear();
            _isSpeechDetected = false;
            _consecutiveSilentFrames = 0;
            _consecutiveSpeechFrames = 0;

            _audioQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(15)
            {
                FullMode = BoundedChannelFullMode.DropOldest
            });

            _workerCts = new CancellationTokenSource();
            _workerTask = Task.Run(() => ProcessTranscriptionQueueAsync(_audioQueue.Reader, _workerCts.Token));

            if (captureSystem)
            {
                _audioManager.SystemAudio.AudioData += OnIncomingAudioData;
            }
            if (captureMic)
            {
                _audioManager.Microphone.AudioData += OnIncomingAudioData;
            }
            _isRunning = true;
        }

        string sourceName = _settings.AudioSource switch
        {
            AudioSourceMode.MicrophoneOnly => "Microphone",
            AudioSourceMode.Both => "System Audio + Microphone",
            _ => "System Audio"
        };

        StatusChanged?.Invoke(this, $"Background transcription started using {_engine.Name} ({sourceName}).");
        NotificationRequested?.Invoke(this, "Background transcription started");
        StateChanged?.Invoke(this, true);

        return true;
    }

    /// <summary>
    /// Stops continuous background audio transcription and releases dedicated resources.
    /// Drains queued transcription jobs, copies complete transcript to clipboard, and shows notification.
    /// </summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? ctsToCancel;
        Task? taskToWait;
        Channel<byte[]>? queueToComplete;

        lock (_syncLock)
        {
            if (!_isRunning) return;
            _isRunning = false;

            _audioManager.SystemAudio.AudioData -= OnIncomingAudioData;
            _audioManager.Microphone.AudioData -= OnIncomingAudioData;

            ctsToCancel = _workerCts;
            taskToWait = _workerTask;
            queueToComplete = _audioQueue;

            _workerCts = null;
            _workerTask = null;
            _audioQueue = null;

            // Check remaining audio chunk (flush any remaining valid speech >= 300ms)
            if (_currentChunkSamples.Count >= (AudioChunkConverter.TargetSampleRate * 0.3))
            {
                try
                {
                    byte[] wav = AudioChunkConverter.Create16kWav(_currentChunkSamples.ToArray());
                    queueToComplete?.Writer.TryWrite(wav);
                }
                catch { }
            }
            _currentChunkSamples.Clear();
            _preRollBuffer.Clear();
            CurrentPeakLevel = 0f;
            AudioLevelChanged?.Invoke(this, 0f);
        }

        // Complete channel so worker drains remaining in-flight audio chunks
        queueToComplete?.Writer.TryComplete();

        // Wait with a sensible timeout for already queued transcription jobs to finish
        if (taskToWait != null)
        {
            try
            {
                var delayTask = Task.Delay(3500);
                var completedTask = await Task.WhenAny(taskToWait, delayTask);
                if (completedTask == delayTask)
                {
                    // Timed out waiting for queued audio transcription to finish, cancel worker
                    ctsToCancel?.Cancel();
                    try { await Task.WhenAny(taskToWait, Task.Delay(500)); } catch { }
                }
            }
            catch { }
        }
        ctsToCancel?.Dispose();

        // 4. Release shared audio capture IF exclusively started by transcription service
        if (_startedSystemAudioExclusively)
        {
            _startedSystemAudioExclusively = false;
            bool generalCaptureEnabled = _settings.CaptureSystemAudio;
            bool virtualRoutingActive = (_settings.RouteSystemToVirtual || _settings.RouteToVirtualAudio) && _audioManager.VirtualBridge.IsActive;
            bool physicalRoutingActive = _settings.RouteSystemToPhysical && _audioManager.PhysicalBridge.IsActive;

            if (!generalCaptureEnabled && !virtualRoutingActive && !physicalRoutingActive)
            {
                try
                {
                    await _audioManager.SystemAudio.StopAsync();
                }
                catch { }
            }
        }

        if (_startedMicCaptureExclusively)
        {
            _startedMicCaptureExclusively = false;
            bool micRoutingVirtual = _settings.RouteMicToVirtual && _audioManager.VirtualBridge.IsActive;
            bool micRoutingPhysical = _settings.RouteMicToPhysical && _audioManager.PhysicalBridge.IsActive;

            if (!micRoutingVirtual && !micRoutingPhysical && _settings.AudioSource == AudioSourceMode.SystemAudioOnly)
            {
                try
                {
                    await _audioManager.Microphone.StopAsync();
                }
                catch { }
            }
        }

        // Collect finalized session transcript and log diagnostics WITHOUT logging actual transcript
        int segmentCount;
        int characterCount;
        string fullTranscript;

        lock (_syncLock)
        {
            segmentCount = _sessionSegments.Count;
            fullTranscript = string.Join(Environment.NewLine, _sessionSegments);
            characterCount = fullTranscript.Length;
        }

        // Diagnostic log: count of recognized segments and total transcript characters without logging text
        System.Diagnostics.Debug.WriteLine($"[AudioTranscriptionService] Session ended with {segmentCount} segments, {characterCount} total characters.");

        // Copy complete transcript to Windows clipboard on UI thread
        if (string.IsNullOrWhiteSpace(fullTranscript))
        {
            StatusChanged?.Invoke(this, "Background transcription stopped (no speech recognized).");
            NotificationRequested?.Invoke(this, "Transcription stopped — no text to copy");
        }
        else
        {
            bool copySuccess = ClipboardHelper.TrySetText(fullTranscript, out string? clipError);
            if (copySuccess)
            {
                StatusChanged?.Invoke(this, $"Background transcription stopped. Copied {segmentCount} segments ({characterCount} chars) to clipboard.");
                NotificationRequested?.Invoke(this, "Transcription stopped — transcript copied to clipboard");
            }
            else
            {
                StatusChanged?.Invoke(this, $"Background transcription stopped. Clipboard copy failed: {clipError}. Transcript preserved.");
                NotificationRequested?.Invoke(this, $"Transcription stopped — failed to copy to clipboard: {clipError}");
            }
        }

        StateChanged?.Invoke(this, false);
    }

    /// <summary>
    /// Processes captured audio frames from SystemAudio or Microphone.
    /// Decodes to mono, resamples to 16kHz, performs VAD/silence chunking.
    /// </summary>
    private void OnIncomingAudioData(object? sender, AudioDataEventArgs e)
    {
        if (!_isRunning || e.BytesRecorded <= 0) return;

        try
        {
            float[] monoSamples = AudioChunkConverter.DecodeToMonoFloatSamples(e.Buffer, e.BytesRecorded, e.WaveFormat);
            if (monoSamples.Length == 0) return;

            float[] resampled16k = AudioChunkConverter.Resample(monoSamples, e.WaveFormat.SampleRate, AudioChunkConverter.TargetSampleRate);
            if (resampled16k.Length == 0) return;

            float peak = AudioChunkConverter.CalculatePeak(resampled16k);
            CurrentPeakLevel = peak;
            AudioLevelChanged?.Invoke(this, peak);

            float threshold = Math.Max(0.003f, _settings.TranscriptionSilenceThreshold);

            byte[]? readyWavChunk = null;

            lock (_syncLock)
            {
                if (!_isRunning) return;

                bool isCurrentFrameSpeech = peak >= threshold;

                if (isCurrentFrameSpeech)
                {
                    _consecutiveSpeechFrames++;
                    _consecutiveSilentFrames = 0;

                    if (!_isSpeechDetected && _consecutiveSpeechFrames >= 2)
                    {
                        _isSpeechDetected = true;
                        // Prepend pre-roll buffer so starting syllables are retained
                        foreach (float prSample in _preRollBuffer)
                        {
                            _currentChunkSamples.Add(prSample);
                        }
                    }
                }
                else
                {
                    _consecutiveSilentFrames++;
                    _consecutiveSpeechFrames = 0;
                }

                if (_isSpeechDetected)
                {
                    _currentChunkSamples.AddRange(resampled16k);

                    int maxChunkDurationSec = Math.Clamp(_settings.TranscriptionMaxChunkSeconds, 3, 12);
                    int maxSamples = maxChunkDurationSec * AudioChunkConverter.TargetSampleRate;

                    // Condition 1: Pause / Silence detected (~700ms silence at 16kHz)
                    // ~35 frames of 20ms = 700ms
                    bool pauseDetected = (_consecutiveSilentFrames >= 35);

                    // Condition 2: Max duration limit reached
                    bool maxDurationReached = (_currentChunkSamples.Count >= maxSamples);

                    if (pauseDetected || maxDurationReached)
                    {
                        int minSamples = (int)(AudioChunkConverter.TargetSampleRate * 0.35); // Min 350ms of audio

                        if (_currentChunkSamples.Count >= minSamples)
                        {
                            readyWavChunk = AudioChunkConverter.Create16kWav(_currentChunkSamples.ToArray());
                        }

                        _currentChunkSamples.Clear();
                        _isSpeechDetected = false;
                        _consecutiveSilentFrames = 0;
                        _consecutiveSpeechFrames = 0;

                        // If max duration was reached, preserve brief overlap (150ms)
                        if (maxDurationReached)
                        {
                            int overlap = Math.Min(resampled16k.Length, 2400);
                            for (int i = resampled16k.Length - overlap; i < resampled16k.Length; i++)
                            {
                                _currentChunkSamples.Add(resampled16k[i]);
                            }
                            _isSpeechDetected = true;
                        }
                    }
                }
                else
                {
                    // Maintain pre-roll rolling buffer
                    foreach (float s in resampled16k)
                    {
                        _preRollBuffer.Enqueue(s);
                        if (_preRollBuffer.Count > PreRollSampleCapacity)
                        {
                            _preRollBuffer.Dequeue();
                        }
                    }
                }
            }

            if (readyWavChunk != null && _audioQueue != null)
            {
                _audioQueue.Writer.TryWrite(readyWavChunk);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AudioTranscriptionService] Buffer chunk error: {ex.Message}");
        }
    }

    /// <summary>
    /// Background consumer task taking WAV audio chunks, sending them to the STT engine,
    /// and injecting recognized text into the active chat input.
    /// </summary>
    private async Task ProcessTranscriptionQueueAsync(ChannelReader<byte[]> reader, CancellationToken ct)
    {
        try
        {
            while (await reader.WaitToReadAsync(ct))
            {
                while (reader.TryRead(out byte[]? wavData))
                {
                    if (ct.IsCancellationRequested) break;
                    if (wavData == null || wavData.Length <= 44) continue;

                    ISpeechToTextEngine? engine;
                    lock (_syncLock)
                    {
                        engine = _engine;
                    }

                    if (engine == null || !engine.IsConfigured) continue;

                    string? recognizedText = null;
                    try
                    {
                        recognizedText = await engine.TranscribeWavAsync(wavData, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        StatusChanged?.Invoke(this, $"[Audio-to-Text Engine Error] {ex.Message}");
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(recognizedText)) continue;

                    // Thread-safely accumulate finalized speech segment and reject duplicates
                    bool added = TryAddSessionSegment(recognizedText);
                    if (!added)
                    {
                        StatusChanged?.Invoke(this, $"[Audio-to-Text] Duplicate segment ignored: \"{Truncate(recognizedText.Trim(), 40)}\"");
                        continue;
                    }

                    string segment = recognizedText.Trim();
                    StatusChanged?.Invoke(this, $"Recognized: \"{segment}\"");
                    TranscriptionReceived?.Invoke(this, new TranscriptionEventArgs(segment, isFinal: true));

                    // Dispatch to active browser tab (independent of clipboard copying)
                    await DispatchToChatInputAsync(segment);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on worker shutdown/timeout
        }
    }

    /// <summary>
    /// Injects recognized text into the active ChatGPT or Gemini tab via ChatInputInjector.
    /// If no valid input is located, notifies the user so transcription is never lost.
    /// </summary>
    private async Task DispatchToChatInputAsync(string text)
    {
        BrowserPanel? panel = _browserPanelProvider();
        if (panel == null)
        {
            NotificationRequested?.Invoke(this, $"[Audio-to-Text] Recognized text preserved: \"{Truncate(text, 50)}\"");
            return;
        }

        try
        {
            ChatInjectionResult result = await ChatInputInjector.InjectTextAsync(panel, text);

            if (!result.Success)
            {
                string reason = result.Reason ?? "Chat input box not found.";
                StatusChanged?.Invoke(this, $"[Audio-to-Text Notice] {reason}");
                NotificationRequested?.Invoke(this, $"[Audio-to-Text] Note: {reason} Text: \"{Truncate(text, 40)}\"");
            }
            else
            {
                StatusChanged?.Invoke(this, $"Appended to {result.Site ?? "Chat"}: \"{Truncate(text, 40)}\"");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AudioTranscriptionService] Chat dispatch error: {ex.Message}");
        }
    }

    private static string Truncate(string str, int maxLen)
    {
        if (string.IsNullOrEmpty(str)) return string.Empty;
        return str.Length <= maxLen ? str : str.Substring(0, maxLen - 1) + "…";
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            if (_isDisposed) return;
            _isDisposed = true;
        }

        _ = StopAsync();

        lock (_syncLock)
        {
            _engine?.Dispose();
            _engine = null;
        }
    }
}
