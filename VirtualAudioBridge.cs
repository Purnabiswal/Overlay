using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MyOverlay;

/// <summary>
/// Bridges system loopback audio into a Virtual Audio Device's playback endpoint
/// so that the Virtual Audio Device's recording endpoint acts as a live microphone
/// for Chromium / WebView2 / ChatGPT getUserMedia().
/// Also supports VoiceMeeter-style physical output bridging (sidetone/monitoring).
/// </summary>
public sealed class VirtualAudioBridge : IDisposable
{
    private static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");

    private readonly object _syncLock = new();
    private WasapiOut? _wasapiOut;
    private JitterBufferWaveProvider? _jitterBuffer;
    private MonitoringWaveProvider? _monitoringProvider;
    private WaveFormat? _currentFormat;
    private WaveFormat? _outputFormat;
    private string? _targetDeviceId;
    private string? _targetDeviceName;
    private bool _isActive;
    private bool _isDisposed;
    private bool _isFeedbackBlocked;
    private float _currentPeak;

    // Diagnostics & Metrics
    private long _totalBytesFed;
    private long _totalBytesRead;

    // Rate calculation windows
    private long _fedWindowBytes;
    private long _fedWindowLastTick = Stopwatch.GetTimestamp();
    private double _inputBytesPerSecond;

    private long _readWindowBytes;
    private long _readWindowLastTick = Stopwatch.GetTimestamp();
    private double _outputBytesPerSecond;

    public string BridgeName { get; set; } = "Virtual Audio Bridge";
    public float Volume { get; set; } = 1.0f;
    public bool IsMuted { get; set; } = false;

    public bool IsActive
    {
        get
        {
            lock (_syncLock) return _isActive;
        }
    }

    public bool IsFeedbackBlocked
    {
        get
        {
            lock (_syncLock) return _isFeedbackBlocked;
        }
    }

    public string? TargetDeviceId
    {
        get
        {
            lock (_syncLock) return _targetDeviceId;
        }
    }

    public string? TargetDeviceName
    {
        get
        {
            lock (_syncLock) return _targetDeviceName;
        }
    }

    public WaveFormat? InputWaveFormat
    {
        get
        {
            lock (_syncLock) return _currentFormat ?? _outputFormat;
        }
    }

    public WaveFormat? OutputWaveFormat
    {
        get
        {
            lock (_syncLock) return _outputFormat;
        }
    }

    public float CurrentPeak
    {
        get
        {
            lock (_syncLock) return _currentPeak;
        }
    }

    public long TotalBytesFed => Interlocked.Read(ref _totalBytesFed);
    public long TotalBytesRead => Interlocked.Read(ref _totalBytesRead);
    public int StarvationCount => _jitterBuffer?.UnderrunCount ?? 0;
    public int OverflowCount => _jitterBuffer?.OverflowCount ?? 0;

    public int CurrentBufferedBytes => _jitterBuffer?.BufferedBytes ?? 0;
    public double BufferLatencyMs => _jitterBuffer?.BufferedDurationMs ?? 0.0;
    public double BufferDurationMs => 1000.0;

    public double InputBytesPerSecond
    {
        get
        {
            UpdateInputRate();
            return _inputBytesPerSecond;
        }
    }

    public double OutputBytesPerSecond
    {
        get
        {
            UpdateOutputRate();
            return _outputBytesPerSecond;
        }
    }

    public event EventHandler<float>? BridgeLevelChanged;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<Exception>? BridgeError;

    public void SetFeedbackBlocked(bool blocked)
    {
        lock (_syncLock)
        {
            if (_isFeedbackBlocked == blocked) return;
            _isFeedbackBlocked = blocked;

            if (blocked)
            {
                NotifyStatus("FEEDBACK PREVENTED: Routing blocked because system audio capture and virtual bridge target are identical.");
                _currentPeak = 0.0f;
                BridgeLevelChanged?.Invoke(this, 0.0f);
            }
            else
            {
                NotifyStatus("Feedback clear: Audio routing resumed.");
            }
        }
    }

    public void Start(string virtualPlaybackDeviceId)
    {
        lock (_syncLock)
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(VirtualAudioBridge));

            if (string.IsNullOrWhiteSpace(virtualPlaybackDeviceId) ||
                virtualPlaybackDeviceId.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                virtualPlaybackDeviceId.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                StopInternal();
                return;
            }

            if (_isActive && string.Equals(_targetDeviceId, virtualPlaybackDeviceId, StringComparison.OrdinalIgnoreCase)
                && _wasapiOut != null && _wasapiOut.PlaybackState == PlaybackState.Playing)
            {
                return;
            }

            _targetDeviceId = virtualPlaybackDeviceId;
            InitializeOutputDevice();
        }
    }

    public void FeedAudio(byte[] buffer, int bytesRecorded, WaveFormat waveFormat)
    {
        FeedAudio(buffer, bytesRecorded, waveFormat, AudioSourceType.SystemAudio);
    }

    public void FeedAudio(byte[] buffer, int bytesRecorded, WaveFormat waveFormat, AudioSourceType sourceType)
    {
        if (bytesRecorded <= 0 || !_isActive || _isDisposed || _isFeedbackBlocked)
        {
            return;
        }

        JitterBufferWaveProvider? jb;
        WaveFormat? outFmt;

        lock (_syncLock)
        {
            if (!_isActive || _isDisposed || _isFeedbackBlocked ||
                string.IsNullOrEmpty(_targetDeviceId) ||
                _targetDeviceId.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                _targetDeviceId.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            jb = _jitterBuffer;
            outFmt = _outputFormat;
            _currentFormat = waveFormat;
        }

        if (jb == null || outFmt == null)
        {
            return;
        }

        try
        {
            // Apply bridge volume & mute
            byte[] feedingBuffer = buffer;
            if (IsMuted || Math.Abs(Volume - 1.0f) > 0.001f)
            {
                feedingBuffer = (byte[])buffer.Clone();
                AudioHelper.ApplyVolumeAndMute(feedingBuffer, bytesRecorded, waveFormat, Volume, IsMuted);
            }

            // Convert format if needed
            byte[] convertedBuffer;
            int convertedBytes;

            if (FormatsAreCompatible(waveFormat, outFmt))
            {
                convertedBuffer = feedingBuffer;
                convertedBytes = bytesRecorded;
            }
            else
            {
                convertedBuffer = AudioFormatConverter.Convert(feedingBuffer, bytesRecorded, waveFormat, outFmt);
                convertedBytes = convertedBuffer.Length;
            }

            if (convertedBytes > 0)
            {
                jb.AddSamples(convertedBuffer, 0, convertedBytes, sourceType);
                Interlocked.Add(ref _totalBytesFed, convertedBytes);
                RecordInputBytes(convertedBytes);

                float peak = IsMuted ? 0.0f : SystemAudioCapture.CalculatePeak(convertedBuffer, convertedBytes, outFmt);
                _currentPeak = peak;
                BridgeLevelChanged?.Invoke(this, peak);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{BridgeName}] FeedAudio error: {ex.Message}");
            NotifyError(ex);
        }
    }

    private void InitializeOutputDevice()
    {
        try
        {
            StopOutputOnly();

            if (string.IsNullOrWhiteSpace(_targetDeviceId) ||
                _targetDeviceId.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                _targetDeviceId.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            using var enumerator = new MMDeviceEnumerator();
            MMDevice? targetDevice = null;

            try
            {
                targetDevice = enumerator.GetDevice(_targetDeviceId!);
            }
            catch (Exception ex)
            {
                NotifyStatus($"Target device not found ({_targetDeviceId}): {ex.Message}");
                return;
            }

            if (targetDevice == null || targetDevice.State != DeviceState.Active)
            {
                NotifyStatus($"Target device '{targetDevice?.FriendlyName ?? _targetDeviceId}' is not active ({targetDevice?.State}).");
                return;
            }

            _targetDeviceName = targetDevice.FriendlyName;
            _outputFormat = targetDevice.AudioClient.MixFormat;

            // Stable JitterBuffer: 80ms nominal latency, 200ms max threshold, independent source buffers
            _jitterBuffer = new JitterBufferWaveProvider(_outputFormat, targetBufferMs: 80, maxBufferMs: 200);
            _monitoringProvider = new MonitoringWaveProvider(_jitterBuffer, this);

            // Use WASAPI shared mode with push (polling) sync for virtual and physical audio bridges.
            // Virtual audio drivers (Steam Streaming Microphone, VB-Cable, VAC, Voicemeeter)
            // lack hardware DMA timer interrupts, causing event-sync mode to stall after 1-2 seconds.
            // Push mode uses a multimedia timer and guarantees steady, continuous, real-time audio flow.
            _wasapiOut = new WasapiOut(targetDevice, AudioClientShareMode.Shared, useEventSync: false, latency: 40);
            _wasapiOut.PlaybackStopped += OnPlaybackStopped;
            _wasapiOut.Init(_monitoringProvider);
            _wasapiOut.Play();

            _isActive = true;
            NotifyStatus($"Streaming continuous audio to endpoint: {_targetDeviceName} ({_outputFormat.SampleRate} Hz, {_outputFormat.Channels} ch, {_outputFormat.BitsPerSample} bit)");
        }
        catch (COMException comEx)
        {
            Debug.WriteLine($"[{BridgeName}] COMException in InitializeOutputDevice: 0x{comEx.ErrorCode:X8} - {comEx.Message}");
            NotifyError(new InvalidOperationException($"Could not open audio device '{_targetDeviceName}': {comEx.Message}", comEx));
            StopOutputOnly();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{BridgeName}] Exception in InitializeOutputDevice: {ex.Message}");
            NotifyError(ex);
            StopOutputOnly();
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null && !_isDisposed && _isActive)
        {
            Debug.WriteLine($"[{BridgeName}] Playback stopped with exception: {e.Exception.Message}");
            NotifyStatus($"Playback device disconnected or reset: {e.Exception.Message}");
        }
    }

    public void Stop()
    {
        lock (_syncLock)
        {
            StopInternal();
        }
    }

    private void StopInternal()
    {
        _isActive = false;
        _targetDeviceId = null;
        _targetDeviceName = null;
        _isFeedbackBlocked = false;
        _currentPeak = 0.0f;
        StopOutputOnly();
        BridgeLevelChanged?.Invoke(this, 0.0f);
        NotifyStatus("Audio bridge stopped.");
    }

    private void StopOutputOnly()
    {
        try
        {
            if (_wasapiOut != null)
            {
                _wasapiOut.PlaybackStopped -= OnPlaybackStopped;
                try
                {
                    _wasapiOut.Stop();
                }
                catch { }

                _wasapiOut.Dispose();
                _wasapiOut = null;
            }
        }
        catch { }

        _monitoringProvider = null;
        _jitterBuffer?.Clear();
        _jitterBuffer = null;
        _currentFormat = null;
        _outputFormat = null;
    }

    /// <summary>
    /// Checks whether two WaveFormats are compatible without requiring conversion.
    /// Accurately treats 32-bit IEEE float and Extensible with IEEE float subformat as identical.
    /// </summary>
    public static bool FormatsAreCompatible(WaveFormat a, WaveFormat b)
    {
        if (a.SampleRate != b.SampleRate || a.Channels != b.Channels) return false;
        if (a.BitsPerSample != b.BitsPerSample) return false;

        bool aIsFloat = a.Encoding == WaveFormatEncoding.IeeeFloat ||
                        (a is WaveFormatExtensible wea && wea.SubFormat == SubtypeIeeeFloat);
        bool bIsFloat = b.Encoding == WaveFormatEncoding.IeeeFloat ||
                        (b is WaveFormatExtensible web && web.SubFormat == SubtypeIeeeFloat);

        if (aIsFloat && bIsFloat) return true;

        bool aIsPcm = a.Encoding == WaveFormatEncoding.Pcm ||
                      (a is WaveFormatExtensible weaPcm && weaPcm.SubFormat == SubtypePcm);
        bool bIsPcm = b.Encoding == WaveFormatEncoding.Pcm ||
                      (b is WaveFormatExtensible webPcm && webPcm.SubFormat == SubtypePcm);

        if (aIsPcm && bIsPcm) return true;

        return a.Encoding == b.Encoding;
    }

    private void RecordInputBytes(int bytes)
    {
        Interlocked.Add(ref _fedWindowBytes, bytes);
        UpdateInputRate();
    }

    internal void RecordOutputBytes(int bytes)
    {
        Interlocked.Add(ref _totalBytesRead, bytes);
        Interlocked.Add(ref _readWindowBytes, bytes);
        UpdateOutputRate();
    }

    private void UpdateInputRate()
    {
        long now = Stopwatch.GetTimestamp();
        long elapsed = now - Volatile.Read(ref _fedWindowLastTick);
        double seconds = (double)elapsed / Stopwatch.Frequency;
        if (seconds >= 0.5)
        {
            long bytes = Interlocked.Exchange(ref _fedWindowBytes, 0);
            _fedWindowLastTick = now;
            _inputBytesPerSecond = seconds > 0 ? bytes / seconds : 0;
        }
    }

    private void UpdateOutputRate()
    {
        long now = Stopwatch.GetTimestamp();
        long elapsed = now - Volatile.Read(ref _readWindowLastTick);
        double seconds = (double)elapsed / Stopwatch.Frequency;
        if (seconds >= 0.5)
        {
            long bytes = Interlocked.Exchange(ref _readWindowBytes, 0);
            _readWindowLastTick = now;
            _outputBytesPerSecond = seconds > 0 ? bytes / seconds : 0;
        }
    }

    private void NotifyStatus(string message)
    {
        Debug.WriteLine($"[{BridgeName}] {message}");
        try
        {
            StatusChanged?.Invoke(this, message);
        }
        catch { }
    }

    private void NotifyError(Exception ex)
    {
        Debug.WriteLine($"[{BridgeName} ERROR] {ex}");
        try
        {
            BridgeError?.Invoke(this, ex);
        }
        catch { }
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            StopInternal();
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Wrapper wave provider that tracks actual bytes consumed by WasapiOut.
    /// </summary>
    private sealed class MonitoringWaveProvider : IWaveProvider
    {
        private readonly IWaveProvider _source;
        private readonly VirtualAudioBridge _bridge;

        public MonitoringWaveProvider(IWaveProvider source, VirtualAudioBridge bridge)
        {
            _source = source;
            _bridge = bridge;
        }

        public WaveFormat WaveFormat => _source.WaveFormat;

        public int Read(byte[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            _bridge.RecordOutputBytes(read);
            return read;
        }
    }
}

/// <summary>
/// High-performance, thread-safe Jitter Buffer with independent queues for System Audio and Microphone.
/// Performs sample mixing when multiple sources are active, and guarantees continuous silence output
/// on underruns so WasapiOut never terminates.
/// </summary>
public sealed class JitterBufferWaveProvider : IWaveProvider
{
    private readonly WaveFormat _waveFormat;
    private readonly BufferedWaveProvider _systemBuffer;
    private readonly BufferedWaveProvider _micBuffer;
    private readonly int _maxBufferBytes;
    private readonly int _targetBufferBytes;
    private readonly object _lock = new();

    private bool _isPrerolled;
    private long _totalBytesRead;
    private int _underrunCount;
    private int _overflowCount;

    public WaveFormat WaveFormat => _waveFormat;

    public int BufferedBytes
    {
        get
        {
            lock (_lock)
            {
                return Math.Max(_systemBuffer.BufferedBytes, _micBuffer.BufferedBytes);
            }
        }
    }

    public double BufferedDurationMs
    {
        get
        {
            if (_waveFormat.AverageBytesPerSecond <= 0) return 0;
            return (double)BufferedBytes / _waveFormat.AverageBytesPerSecond * 1000.0;
        }
    }

    public int UnderrunCount => Volatile.Read(ref _underrunCount);
    public int OverflowCount => Volatile.Read(ref _overflowCount);
    public long TotalBytesRead => Interlocked.Read(ref _totalBytesRead);

    public JitterBufferWaveProvider(WaveFormat waveFormat, int targetBufferMs = 60, int maxBufferMs = 600)
    {
        _waveFormat = waveFormat;

        _systemBuffer = new BufferedWaveProvider(waveFormat)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromMilliseconds(3000),
            ReadFully = false
        };

        _micBuffer = new BufferedWaveProvider(waveFormat)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromMilliseconds(3000),
            ReadFully = false
        };

        int bytesPerMs = Math.Max(1, waveFormat.AverageBytesPerSecond / 1000);
        _targetBufferBytes = targetBufferMs * bytesPerMs;
        _maxBufferBytes = maxBufferMs * bytesPerMs;
    }

    public void AddSamples(byte[] buffer, int offset, int count, AudioSourceType sourceType)
    {
        if (count <= 0) return;

        lock (_lock)
        {
            var targetBwp = (sourceType == AudioSourceType.Microphone) ? _micBuffer : _systemBuffer;

            // If drift has accumulated beyond max threshold (600ms), gently trim excess down to target (60ms)
            if (targetBwp.BufferedBytes > _maxBufferBytes)
            {
                int excess = targetBwp.BufferedBytes - _targetBufferBytes;
                if (excess > 0)
                {
                    byte[] discard = new byte[excess];
                    targetBwp.Read(discard, 0, excess);
                    Interlocked.Increment(ref _overflowCount);
                }
            }

            targetBwp.AddSamples(buffer, offset, count);
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        if (count <= 0) return 0;

        byte[] sysTemp = new byte[count];
        byte[] micTemp = new byte[count];
        int sysRead = 0;
        int micRead = 0;

        lock (_lock)
        {
            int maxAvail = Math.Max(_systemBuffer.BufferedBytes, _micBuffer.BufferedBytes);

            // Pre-roll / buffering threshold check to prevent stuttering on partial packets
            if (!_isPrerolled)
            {
                if (maxAvail >= _targetBufferBytes)
                {
                    _isPrerolled = true;
                }
                else
                {
                    // Still pre-rolling: send continuous silence so WasapiOut stays alive
                    Array.Clear(buffer, offset, count);
                    Interlocked.Add(ref _totalBytesRead, count);
                    return count;
                }
            }
            else if (maxAvail == 0)
            {
                _isPrerolled = false;
                Array.Clear(buffer, offset, count);
                Interlocked.Increment(ref _underrunCount);
                Interlocked.Add(ref _totalBytesRead, count);
                return count;
            }

            if (_systemBuffer.BufferedBytes > 0)
            {
                sysRead = _systemBuffer.Read(sysTemp, 0, count);
            }
            if (_micBuffer.BufferedBytes > 0)
            {
                micRead = _micBuffer.Read(micTemp, 0, count);
            }
        }

        if (sysRead > 0 && micRead > 0)
        {
            // Both System Audio and Mic have data: mix them!
            int common = Math.Min(sysRead, micRead);
            Array.Copy(sysTemp, 0, buffer, offset, sysRead);
            AudioHelper.MixBuffers(buffer, offset, micTemp, 0, common, _waveFormat);

            if (micRead > sysRead)
            {
                Array.Copy(micTemp, sysRead, buffer, offset + sysRead, micRead - sysRead);
            }

            int maxRead = Math.Max(sysRead, micRead);
            if (maxRead < count)
            {
                Array.Clear(buffer, offset + maxRead, count - maxRead);
            }
        }
        else if (sysRead > 0)
        {
            Array.Copy(sysTemp, 0, buffer, offset, sysRead);
            if (sysRead < count)
            {
                Array.Clear(buffer, offset + sysRead, count - sysRead);
            }
        }
        else if (micRead > 0)
        {
            Array.Copy(micTemp, 0, buffer, offset, micRead);
            if (micRead < count)
            {
                Array.Clear(buffer, offset + micRead, count - micRead);
            }
        }
        else
        {
            // Silence padding
            Array.Clear(buffer, offset, count);
            Interlocked.Increment(ref _underrunCount);
        }

        Interlocked.Add(ref _totalBytesRead, count);
        return count; // ALWAYS return count so WasapiOut NEVER stops!
    }

    public void Clear()
    {
        lock (_lock)
        {
            _isPrerolled = false;
            _systemBuffer.ClearBuffer();
            _micBuffer.ClearBuffer();
        }
    }
}

/// <summary>
/// Blazing fast, pure-managed audio format conversion for sample rates, channels, and bit depths.
/// </summary>
public static class AudioFormatConverter
{
    private static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    public static byte[] Convert(byte[] input, int bytesRecorded, WaveFormat inFormat, WaveFormat outFormat)
    {
        if (bytesRecorded <= 0 || input.Length < bytesRecorded) return Array.Empty<byte>();

        // 1. Decode to normalized float array [-1.0f .. 1.0f]
        float[] inFloats = DecodeToFloats(input, bytesRecorded, inFormat);
        int inFrames = inFloats.Length / inFormat.Channels;
        if (inFrames <= 0) return Array.Empty<byte>();

        // 2. Channel conversion (Frames remain constant)
        float[] channelFloats;
        if (inFormat.Channels == outFormat.Channels)
        {
            channelFloats = inFloats;
        }
        else if (inFormat.Channels == 1 && outFormat.Channels == 2)
        {
            channelFloats = new float[inFrames * 2];
            for (int i = 0; i < inFrames; i++)
            {
                float s = inFloats[i];
                channelFloats[i * 2] = s;
                channelFloats[i * 2 + 1] = s;
            }
        }
        else if (inFormat.Channels == 2 && outFormat.Channels == 1)
        {
            channelFloats = new float[inFrames];
            for (int i = 0; i < inFrames; i++)
            {
                channelFloats[i] = (inFloats[i * 2] + inFloats[i * 2 + 1]) * 0.5f;
            }
        }
        else
        {
            channelFloats = new float[inFrames * outFormat.Channels];
            for (int i = 0; i < inFrames; i++)
            {
                float s = inFloats[i * inFormat.Channels];
                for (int ch = 0; ch < outFormat.Channels; ch++)
                {
                    channelFloats[i * outFormat.Channels + ch] = s;
                }
            }
        }

        // 3. Sample Rate Conversion via Linear Interpolation
        float[] outFloats;
        int outChannels = outFormat.Channels;
        if (inFormat.SampleRate == outFormat.SampleRate)
        {
            outFloats = channelFloats;
        }
        else
        {
            double ratio = (double)outFormat.SampleRate / inFormat.SampleRate;
            int outFrames = (int)Math.Round(inFrames * ratio);
            outFloats = new float[outFrames * outChannels];

            for (int i = 0; i < outFrames; i++)
            {
                double srcPos = i / ratio;
                int idx0 = (int)Math.Floor(srcPos);
                int idx1 = Math.Min(idx0 + 1, inFrames - 1);
                float frac = (float)(srcPos - idx0);

                for (int ch = 0; ch < outChannels; ch++)
                {
                    int i0 = Math.Clamp(idx0 * outChannels + ch, 0, channelFloats.Length - 1);
                    int i1 = Math.Clamp(idx1 * outChannels + ch, 0, channelFloats.Length - 1);
                    float s0 = channelFloats[i0];
                    float s1 = channelFloats[i1];
                    outFloats[i * outChannels + ch] = s0 + (s1 - s0) * frac;
                }
            }
        }

        // 4. Encode from normalized float array to outFormat bytes
        return EncodeFromFloats(outFloats, outFormat);
    }

    private static float[] DecodeToFloats(byte[] buffer, int length, WaveFormat format)
    {
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (format is WaveFormatExtensible we && we.SubFormat == SubtypeIeeeFloat) ||
                       format.BitsPerSample == 32;

        if (isFloat)
        {
            int count = length / 4;
            float[] floats = new float[count];
            for (int i = 0; i < count; i++)
            {
                int off = i * 4;
                if (off + 4 > length) break;
                float val = BitConverter.ToSingle(buffer, off);
                floats[i] = float.IsNaN(val) ? 0.0f : Math.Clamp(val, -1.0f, 1.0f);
            }
            return floats;
        }
        else if (format.BitsPerSample == 16)
        {
            int count = length / 2;
            float[] floats = new float[count];
            for (int i = 0; i < count; i++)
            {
                int off = i * 2;
                if (off + 2 > length) break;
                short val = BitConverter.ToInt16(buffer, off);
                floats[i] = val / 32768.0f;
            }
            return floats;
        }
        else if (format.BitsPerSample == 24)
        {
            int count = length / 3;
            float[] floats = new float[count];
            for (int i = 0; i < count; i++)
            {
                int off = i * 3;
                if (off + 3 > length) break;
                int val = (buffer[off] << 8) | (buffer[off + 1] << 16) | (buffer[off + 2] << 24);
                floats[i] = val / 2147483648.0f;
            }
            return floats;
        }

        int defaultCount = length / 4;
        float[] defFloats = new float[defaultCount];
        for (int i = 0; i < defaultCount; i++)
        {
            int off = i * 4;
            if (off + 4 > length) break;
            defFloats[i] = BitConverter.ToSingle(buffer, off);
        }
        return defFloats;
    }

    private static byte[] EncodeFromFloats(float[] floats, WaveFormat format)
    {
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (format is WaveFormatExtensible we && we.SubFormat == SubtypeIeeeFloat) ||
                       format.BitsPerSample == 32;

        if (isFloat)
        {
            byte[] bytes = new byte[floats.Length * 4];
            for (int i = 0; i < floats.Length; i++)
            {
                float clamped = Math.Clamp(floats[i], -1.0f, 1.0f);
                byte[] b = BitConverter.GetBytes(clamped);
                bytes[i * 4] = b[0];
                bytes[i * 4 + 1] = b[1];
                bytes[i * 4 + 2] = b[2];
                bytes[i * 4 + 3] = b[3];
            }
            return bytes;
        }
        else if (format.BitsPerSample == 16)
        {
            byte[] bytes = new byte[floats.Length * 2];
            for (int i = 0; i < floats.Length; i++)
            {
                float clamped = Math.Clamp(floats[i], -1.0f, 1.0f);
                short val = (short)Math.Round(clamped * 32767.0);
                bytes[i * 2] = (byte)(val & 0xFF);
                bytes[i * 2 + 1] = (byte)((val >> 8) & 0xFF);
            }
            return bytes;
        }

        byte[] defBytes = new byte[floats.Length * 4];
        for (int i = 0; i < floats.Length; i++)
        {
            byte[] b = BitConverter.GetBytes(floats[i]);
            Buffer.BlockCopy(b, 0, defBytes, i * 4, 4);
        }
        return defBytes;
    }
}

public static class AudioHelper
{
    public static void ApplyVolumeAndMute(byte[] buffer, int bytesRecorded, WaveFormat format, float volume, bool isMuted)
    {
        if (bytesRecorded <= 0 || buffer.Length < bytesRecorded) return;

        if (isMuted || volume <= 0.0f)
        {
            Array.Clear(buffer, 0, bytesRecorded);
            return;
        }

        if (Math.Abs(volume - 1.0f) < 0.001f)
        {
            return;
        }

        if (format.Encoding == WaveFormatEncoding.IeeeFloat || format.BitsPerSample == 32)
        {
            int sampleCount = bytesRecorded / 4;
            for (int i = 0; i < sampleCount; i++)
            {
                int offset = i * 4;
                if (offset + 4 > bytesRecorded) break;
                float sample = BitConverter.ToSingle(buffer, offset);
                sample *= volume;
                sample = Math.Clamp(sample, -1.0f, 1.0f);
                byte[] bytes = BitConverter.GetBytes(sample);
                buffer[offset] = bytes[0];
                buffer[offset + 1] = bytes[1];
                buffer[offset + 2] = bytes[2];
                buffer[offset + 3] = bytes[3];
            }
        }
        else if (format.BitsPerSample == 16)
        {
            int sampleCount = bytesRecorded / 2;
            for (int i = 0; i < sampleCount; i++)
            {
                int offset = i * 2;
                if (offset + 2 > bytesRecorded) break;
                short sample = BitConverter.ToInt16(buffer, offset);
                int scaled = (int)(sample * volume);
                scaled = Math.Clamp(scaled, short.MinValue, short.MaxValue);
                short result = (short)scaled;
                buffer[offset] = (byte)(result & 0xFF);
                buffer[offset + 1] = (byte)((result >> 8) & 0xFF);
            }
        }
    }

    public static void MixBuffers(byte[] dest, int destOffset, byte[] src, int srcOffset, int bytes, WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat || format.BitsPerSample == 32)
        {
            int sampleCount = bytes / 4;
            for (int i = 0; i < sampleCount; i++)
            {
                int dOff = destOffset + i * 4;
                int sOff = srcOffset + i * 4;
                if (dOff + 4 > dest.Length || sOff + 4 > src.Length) break;
                float s1 = BitConverter.ToSingle(dest, dOff);
                float s2 = BitConverter.ToSingle(src, sOff);
                float mixed = Math.Clamp(s1 + s2, -1.0f, 1.0f);
                byte[] b = BitConverter.GetBytes(mixed);
                dest[dOff] = b[0];
                dest[dOff + 1] = b[1];
                dest[dOff + 2] = b[2];
                dest[dOff + 3] = b[3];
            }
        }
        else if (format.BitsPerSample == 16)
        {
            int sampleCount = bytes / 2;
            for (int i = 0; i < sampleCount; i++)
            {
                int dOff = destOffset + i * 2;
                int sOff = srcOffset + i * 2;
                if (dOff + 2 > dest.Length || sOff + 2 > src.Length) break;
                short s1 = BitConverter.ToInt16(dest, dOff);
                short s2 = BitConverter.ToInt16(src, sOff);
                int mixed = Math.Clamp(s1 + s2, short.MinValue, short.MaxValue);
                short result = (short)mixed;
                dest[dOff] = (byte)(result & 0xFF);
                dest[dOff + 1] = (byte)((result >> 8) & 0xFF);
            }
        }
    }
}
