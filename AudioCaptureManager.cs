using System;

namespace MyOverlay;

/// <summary>
/// Audio Pipeline Manager coordinating System Audio (WASAPI loopback), Microphone capture,
/// and the Virtual Audio Bridge that routes system audio into a virtual microphone for WebView2.
/// </summary>
public sealed class AudioCaptureManager : IDisposable
{
    private readonly object _syncLock = new();
    private bool _isDisposed;

    public SystemAudioCapture SystemAudio { get; }
    public MicrophoneAudioCapture Microphone { get; }
    public VirtualAudioBridge VirtualBridge { get; }
    public VirtualAudioBridge PhysicalBridge { get; }

    // Routing Matrix
    public bool RouteSystemToVirtual { get; set; } = true;
    public bool RouteSystemToPhysical { get; set; } = false;
    public bool RouteMicToVirtual { get; set; } = true;
    public bool RouteMicToPhysical { get; set; } = false;

    // Volume and Mute Controls
    public float SystemAudioVolume { get; set; } = 1.0f;
    public bool SystemAudioMuted { get; set; } = false;
    public float MicrophoneVolume { get; set; } = 1.0f;
    public bool MicrophoneMuted { get; set; } = false;

    public float VirtualOutputVolume
    {
        get => VirtualBridge.Volume;
        set => VirtualBridge.Volume = value;
    }
    public bool VirtualOutputMuted
    {
        get => VirtualBridge.IsMuted;
        set => VirtualBridge.IsMuted = value;
    }

    public float PhysicalOutputVolume
    {
        get => PhysicalBridge.Volume;
        set => PhysicalBridge.Volume = value;
    }
    public bool PhysicalOutputMuted
    {
        get => PhysicalBridge.IsMuted;
        set => PhysicalBridge.IsMuted = value;
    }

    /// <summary>
    /// Event raised whenever PCM audio data arrives from any active source (tagged with AudioSourceType).
    /// </summary>
    public event EventHandler<AudioDataEventArgs>? AudioDataReceived;

    /// <summary>
    /// Event raised with normalized peak level [0.0 - 1.0] for system audio VU meter.
    /// </summary>
    public event EventHandler<float>? SystemAudioLevelChanged;

    /// <summary>
    /// Event raised with normalized peak level [0.0 - 1.0] for microphone VU meter.
    /// </summary>
    public event EventHandler<float>? MicrophoneLevelChanged;

    /// <summary>
    /// Event raised when status changes on any audio capture or routing component.
    /// </summary>
    public event EventHandler<string>? StatusChanged;

    /// <summary>
    /// Event raised when an error occurs on any audio capture or routing component.
    /// </summary>
    public event EventHandler<Exception>? ErrorOccurred;

    public AudioCaptureManager(AppSettings? settings = null)
    {
        SystemAudio = new SystemAudioCapture();
        Microphone = new MicrophoneAudioCapture();
        VirtualBridge = new VirtualAudioBridge { BridgeName = "Virtual Audio Bridge (Browser / ChatGPT)" };
        PhysicalBridge = new VirtualAudioBridge { BridgeName = "Physical Output Bridge (Headphones / Speakers)" };

        // System Audio events
        SystemAudio.AudioData += OnSystemAudioData;
        SystemAudio.StatusChanged += (s, msg) => StatusChanged?.Invoke(this, $"[System Audio] {msg}");
        SystemAudio.CaptureError += (s, ex) => ErrorOccurred?.Invoke(this, ex);
        SystemAudio.DeviceChanged += OnAudioDevicesChanged;

        // Microphone events
        Microphone.AudioData += OnMicrophoneData;
        Microphone.StatusChanged += (s, msg) => StatusChanged?.Invoke(this, $"[Microphone] {msg}");
        Microphone.CaptureError += (s, ex) => ErrorOccurred?.Invoke(this, ex);

        // Virtual Audio Bridge events
        VirtualBridge.StatusChanged += (s, msg) => StatusChanged?.Invoke(this, $"[Virtual Bridge] {msg}");
        VirtualBridge.BridgeError += (s, ex) => ErrorOccurred?.Invoke(this, ex);

        // Physical Audio Bridge events
        PhysicalBridge.StatusChanged += (s, msg) => StatusChanged?.Invoke(this, $"[Physical Bridge] {msg}");
        PhysicalBridge.BridgeError += (s, ex) => ErrorOccurred?.Invoke(this, ex);

        if (settings != null)
        {
            ApplySettings(settings);
        }
    }

    private void OnSystemAudioData(object? sender, AudioDataEventArgs e)
    {
        byte[] procBuffer = (byte[])e.Buffer.Clone();
        AudioHelper.ApplyVolumeAndMute(procBuffer, e.BytesRecorded, e.WaveFormat, SystemAudioVolume, SystemAudioMuted);

        float peak = SystemAudioMuted ? 0.0f : SystemAudioCapture.CalculatePeak(procBuffer, e.BytesRecorded, e.WaveFormat);
        SystemAudioLevelChanged?.Invoke(this, peak);

        // 1. Route to Virtual Mic if enabled
        if (RouteSystemToVirtual && VirtualBridge.IsActive && !VirtualBridge.IsFeedbackBlocked)
        {
            VirtualBridge.FeedAudio(procBuffer, e.BytesRecorded, e.WaveFormat, AudioSourceType.SystemAudio);
        }

        // 2. Route to Physical Output if enabled
        if (RouteSystemToPhysical && PhysicalBridge.IsActive && !PhysicalBridge.IsFeedbackBlocked)
        {
            PhysicalBridge.FeedAudio(procBuffer, e.BytesRecorded, e.WaveFormat, AudioSourceType.SystemAudio);
        }

        AudioDataReceived?.Invoke(this, e);
    }

    private void OnMicrophoneData(object? sender, AudioDataEventArgs e)
    {
        byte[] procBuffer = (byte[])e.Buffer.Clone();
        AudioHelper.ApplyVolumeAndMute(procBuffer, e.BytesRecorded, e.WaveFormat, MicrophoneVolume, MicrophoneMuted);

        float peak = MicrophoneMuted ? 0.0f : SystemAudioCapture.CalculatePeak(procBuffer, e.BytesRecorded, e.WaveFormat);
        MicrophoneLevelChanged?.Invoke(this, peak);

        // 1. Route to Virtual Mic (Browser / ChatGPT) if enabled
        if (RouteMicToVirtual && VirtualBridge.IsActive)
        {
            VirtualBridge.FeedAudio(procBuffer, e.BytesRecorded, e.WaveFormat, AudioSourceType.Microphone);
        }

        // 2. Route to Physical Output (Monitor / Sidetone) if enabled
        if (RouteMicToPhysical && PhysicalBridge.IsActive && !PhysicalBridge.IsFeedbackBlocked)
        {
            PhysicalBridge.FeedAudio(procBuffer, e.BytesRecorded, e.WaveFormat, AudioSourceType.Microphone);
        }

        AudioDataReceived?.Invoke(this, e);
    }

    private void OnAudioDevicesChanged(object? sender, EventArgs e)
    {
        if (VirtualBridge.IsActive)
        {
            bool feedback = AudioDeviceDiagnostics.IsFeedbackLoop(SystemAudio.CurrentDeviceId, VirtualBridge.TargetDeviceId);
            VirtualBridge.SetFeedbackBlocked(feedback);
        }

        if (PhysicalBridge.IsActive)
        {
            bool feedback = AudioDeviceDiagnostics.IsFeedbackLoop(SystemAudio.CurrentDeviceId, PhysicalBridge.TargetDeviceId);
            PhysicalBridge.SetFeedbackBlocked(feedback);
        }
    }

    /// <summary>
    /// Applies updated configuration to system audio, microphone, and routing matrix.
    /// </summary>
    public void ApplySettings(AppSettings settings)
    {
        lock (_syncLock)
        {
            if (_isDisposed) return;

            // Update routing matrix flags
            RouteSystemToVirtual = settings.RouteSystemToVirtual;
            RouteSystemToPhysical = settings.RouteSystemToPhysical;
            RouteMicToVirtual = settings.RouteMicToVirtual;
            RouteMicToPhysical = settings.RouteMicToPhysical;

            // Update volumes and mutes
            SystemAudioVolume = settings.SystemAudioVolume;
            SystemAudioMuted = settings.SystemAudioMuted;
            MicrophoneVolume = settings.MicrophoneVolume;
            MicrophoneMuted = settings.MicrophoneMuted;
            VirtualBridge.Volume = settings.VirtualOutputVolume;
            VirtualBridge.IsMuted = settings.VirtualOutputMuted;
            PhysicalBridge.Volume = settings.PhysicalOutputVolume;
            PhysicalBridge.IsMuted = settings.PhysicalOutputMuted;

            // 1. Configure System Audio Capture
            SystemAudio.SetDevice(settings.SystemAudioDeviceId);

            bool isVirtualOff = string.IsNullOrWhiteSpace(settings.VirtualAudioPlaybackDeviceId) ||
                                settings.VirtualAudioPlaybackDeviceId.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                                settings.VirtualAudioPlaybackDeviceId.Equals("none", StringComparison.OrdinalIgnoreCase);

            bool isPhysicalOff = string.IsNullOrWhiteSpace(settings.PhysicalOutputDeviceId) ||
                                 settings.PhysicalOutputDeviceId.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                                 settings.PhysicalOutputDeviceId.Equals("none", StringComparison.OrdinalIgnoreCase);

            bool systemRoutingActive = ((RouteSystemToVirtual || settings.RouteToVirtualAudio) && !isVirtualOff) ||
                                       (RouteSystemToPhysical && !isPhysicalOff);
            bool shouldCaptureSystem = settings.CaptureSystemAudio || systemRoutingActive;

            if (shouldCaptureSystem)
            {
                if (!SystemAudio.IsCapturing)
                {
                    SystemAudio.Start();
                }
            }
            else
            {
                if (SystemAudio.IsCapturing)
                {
                    SystemAudio.Stop();
                }
            }

            // 2. Configure Microphone Capture
            bool micRoutingActive = (RouteMicToVirtual && !isVirtualOff) || (RouteMicToPhysical && !isPhysicalOff);
            bool shouldCaptureMic = micRoutingActive ||
                                   settings.AudioSource == AudioSourceMode.MicrophoneOnly ||
                                   settings.AudioSource == AudioSourceMode.Both;

            if (shouldCaptureMic)
            {
                Microphone.SetDevice(settings.MicrophoneDeviceId);
                if (!Microphone.IsCapturing)
                {
                    Microphone.Start();
                }
            }
            else
            {
                if (Microphone.IsCapturing)
                {
                    Microphone.Stop();
                }
            }

            // 3. Configure Virtual Audio Bridge Routing (Browser / ChatGPT - Output Bus A)
            bool shouldRunVirtualBridge = !isVirtualOff && (settings.RouteToVirtualAudio || (shouldCaptureSystem && RouteSystemToVirtual) || (shouldCaptureMic && RouteMicToVirtual));
            string? virtualDeviceId = settings.VirtualAudioPlaybackDeviceId;

            if (shouldRunVirtualBridge)
            {
                bool feedback = AudioDeviceDiagnostics.IsFeedbackLoop(
                    settings.SystemAudioDeviceId ?? SystemAudio.CurrentDeviceId,
                    virtualDeviceId!
                );

                VirtualBridge.SetFeedbackBlocked(feedback);

                if (feedback)
                {
                    StatusChanged?.Invoke(this, "[Virtual Bridge] FEEDBACK LOOP PREVENTED: Cannot route system audio into the same device being captured.");
                }
                else
                {
                    VirtualBridge.Start(virtualDeviceId!);

                    // Ensure target virtual playback endpoint is unmuted and at 100% volume
                    try
                    {
                        using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                        var pbDev = enumerator.GetDevice(virtualDeviceId!);
                        if (pbDev != null)
                        {
                            if (pbDev.AudioEndpointVolume.Mute) pbDev.AudioEndpointVolume.Mute = false;
                            if (pbDev.AudioEndpointVolume.MasterVolumeLevelScalar < 0.95f)
                            {
                                pbDev.AudioEndpointVolume.MasterVolumeLevelScalar = 1.0f;
                            }
                        }
                    }
                    catch { }

                    var paired = AudioDeviceDiagnostics.FindPairedVirtualRecordingDevice(virtualDeviceId!);
                    if (paired != null)
                    {
                        // Ensure paired microphone is unmuted and at 100% volume in Windows so Chromium receives normal strong signal
                        try
                        {
                            using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                            var recDev = enumerator.GetDevice(paired.Id);
                            if (recDev != null)
                            {
                                if (recDev.AudioEndpointVolume.Mute)
                                {
                                    recDev.AudioEndpointVolume.Mute = false;
                                    StatusChanged?.Invoke(this, $"[Virtual Bridge] Unmuted paired virtual microphone '{paired.Name}'.");
                                }
                                if (recDev.AudioEndpointVolume.MasterVolumeLevelScalar < 0.95f)
                                {
                                    recDev.AudioEndpointVolume.MasterVolumeLevelScalar = 1.0f;
                                    StatusChanged?.Invoke(this, $"[Virtual Bridge] Set paired virtual microphone '{paired.Name}' volume to 100%.");
                                }
                            }
                        }
                        catch { }

                        // If AutoSwitchDefaultRecordingDevice is enabled, switch Windows default mic to paired virtual mic
                        if (settings.AutoSwitchDefaultRecordingDevice)
                        {
                            bool ok = AudioDeviceDiagnostics.SetDefaultRecordingDevice(paired.Id);
                            if (ok)
                            {
                                StatusChanged?.Invoke(this, $"[Virtual Bridge] Switched Windows Default Recording Device to '{paired.Name}' for WebView2 / Chromium getUserMedia.");
                            }
                        }
                    }
                }
            }
            else
            {
                VirtualBridge.Stop();

                if (AudioDeviceDiagnostics.HasSwitchedDefaultRecordingDevice)
                {
                    AudioDeviceDiagnostics.RestoreOriginalDefaultRecordingDevice();
                    StatusChanged?.Invoke(this, $"[Virtual Bridge] Restored original Windows Default Recording Device: '{AudioDeviceDiagnostics.OriginalDefaultRecordingDeviceName ?? "Default"}'.");
                }
            }

            // 4. Configure Physical Output Bridge (Headphones / Speakers - Output Bus B)
            bool shouldRunPhysicalBridge = !isPhysicalOff && (RouteSystemToPhysical || RouteMicToPhysical);
            string? physicalDeviceId = settings.PhysicalOutputDeviceId;

            if (shouldRunPhysicalBridge)
            {
                string targetPhysId = physicalDeviceId!;
                if (targetPhysId.Equals("default", StringComparison.OrdinalIgnoreCase))
                {
                    targetPhysId = AudioDeviceDiagnostics.GetDefaultPlaybackDevice()?.Id ?? targetPhysId;
                }

                // Never route to an off/none device or fallback if it resolved to off/none
                if (string.IsNullOrWhiteSpace(targetPhysId) ||
                    targetPhysId.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                    targetPhysId.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    PhysicalBridge.Stop();
                }
                else
                {
                    bool feedback = AudioDeviceDiagnostics.IsFeedbackLoop(
                        settings.SystemAudioDeviceId ?? SystemAudio.CurrentDeviceId,
                        targetPhysId
                    );

                    PhysicalBridge.SetFeedbackBlocked(feedback);
                    PhysicalBridge.Start(targetPhysId);
                }
            }
            else
            {
                PhysicalBridge.Stop();
            }
        }
    }

    /// <summary>
    /// Stops all audio capture and bridge streams.
    /// </summary>
    public void StopAll()
    {
        lock (_syncLock)
        {
            SystemAudio.Stop();
            Microphone.Stop();
            VirtualBridge.Stop();
            PhysicalBridge.Stop();
        }
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            if (_isDisposed) return;
            _isDisposed = true;

            // Restore original default recording device if it was switched
            try
            {
                if (AudioDeviceDiagnostics.HasSwitchedDefaultRecordingDevice)
                {
                    AudioDeviceDiagnostics.RestoreOriginalDefaultRecordingDevice();
                }
            }
            catch { }

            SystemAudio.Dispose();
            Microphone.Dispose();
            VirtualBridge.Dispose();
            PhysicalBridge.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
