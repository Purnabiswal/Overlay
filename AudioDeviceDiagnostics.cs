using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;

namespace MyOverlay;

/// <summary>
/// Audio diagnostics and helper utilities for Windows playback and recording endpoints.
/// Identifies physical microphones, Windows Stereo Mix, and virtual audio devices.
/// </summary>
public static class AudioDeviceDiagnostics
{
    public static AudioDeviceInfo? GetDefaultPlaybackDevice()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var dev = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (dev != null)
            {
                return new AudioDeviceInfo
                {
                    Id = dev.ID,
                    Name = dev.FriendlyName,
                    IsDefault = true,
                    IsPlayback = true,
                    State = dev.State,
                    Classification = AudioDeviceInfo.Classify(dev.FriendlyName, true)
                };
            }
        }
        catch
        {
        }
        return null;
    }

    public static AudioDeviceInfo? GetDefaultRecordingDevice()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var dev = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            if (dev == null)
            {
                dev = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            }

            if (dev != null)
            {
                return new AudioDeviceInfo
                {
                    Id = dev.ID,
                    Name = dev.FriendlyName,
                    IsDefault = true,
                    IsPlayback = false,
                    State = dev.State,
                    Classification = AudioDeviceInfo.Classify(dev.FriendlyName, false)
                };
            }
        }
        catch
        {
        }
        return null;
    }

    public static List<AudioDeviceInfo> GetAllRecordingDevices(bool activeOnly = false)
    {
        var result = new List<AudioDeviceInfo>();

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string? defaultId = null;

            try
            {
                var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                defaultId = defaultDevice?.ID;
            }
            catch
            {
            }

            var mask = activeOnly ? DeviceState.Active : DeviceState.All;
            var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, mask);

            foreach (var ep in endpoints)
            {
                try
                {
                    bool isDef = defaultId != null && ep.ID.Equals(defaultId, StringComparison.OrdinalIgnoreCase);
                    result.Add(new AudioDeviceInfo
                    {
                        Id = ep.ID,
                        Name = ep.FriendlyName,
                        IsDefault = isDef,
                        IsPlayback = false,
                        State = ep.State,
                        Classification = AudioDeviceInfo.Classify(ep.FriendlyName, false)
                    });
                }
                catch
                {
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioDeviceDiagnostics] Error enumerating recording devices: {ex.Message}");
        }

        return result;
    }

    public static List<AudioDeviceInfo> GetAllPlaybackDevices(bool activeOnly = false)
    {
        var result = new List<AudioDeviceInfo>();

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string? defaultId = null;

            try
            {
                var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                defaultId = defaultDevice?.ID;
            }
            catch
            {
            }

            var mask = activeOnly ? DeviceState.Active : DeviceState.All;
            var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, mask);

            foreach (var ep in endpoints)
            {
                try
                {
                    bool isDef = defaultId != null && ep.ID.Equals(defaultId, StringComparison.OrdinalIgnoreCase);
                    result.Add(new AudioDeviceInfo
                    {
                        Id = ep.ID,
                        Name = ep.FriendlyName,
                        IsDefault = isDef,
                        IsPlayback = true,
                        State = ep.State,
                        Classification = AudioDeviceInfo.Classify(ep.FriendlyName, true)
                    });
                }
                catch
                {
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioDeviceDiagnostics] Error enumerating playback devices: {ex.Message}");
        }

        return result;
    }

    public static List<AudioDeviceInfo> GetVirtualPlaybackDevices()
    {
        var all = GetAllPlaybackDevices(activeOnly: true);
        var result = new List<AudioDeviceInfo>();

        foreach (var dev in all)
        {
            if (dev.Classification == AudioDeviceClassification.VirtualDevice)
            {
                result.Add(dev);
            }
        }

        return result;
    }

    public static List<AudioDeviceInfo> GetVirtualRecordingDevices()
    {
        var all = GetAllRecordingDevices(activeOnly: true);
        var result = new List<AudioDeviceInfo>();

        foreach (var dev in all)
        {
            if (dev.Classification == AudioDeviceClassification.VirtualDevice)
            {
                result.Add(dev);
            }
        }

        return result;
    }

    /// <summary>
    /// Checks whether an audio device is a known virtual device based on name and classification.
    /// </summary>
    public static bool IsKnownVirtualAudioDevice(string name, string? id = null)
    {
        return AudioDeviceInfo.Classify(name, true) == AudioDeviceClassification.VirtualDevice ||
               AudioDeviceInfo.Classify(name, false) == AudioDeviceClassification.VirtualDevice;
    }

    /// <summary>
    /// Attempts to find the paired virtual recording device that receives audio
    /// sent to the specified virtual playback device.
    /// </summary>
    public static AudioDeviceInfo? FindPairedVirtualRecordingDevice(string? virtualPlaybackDeviceId)
    {
        if (string.IsNullOrWhiteSpace(virtualPlaybackDeviceId) ||
            virtualPlaybackDeviceId.Equals("off", StringComparison.OrdinalIgnoreCase) ||
            virtualPlaybackDeviceId.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var virtualPlaybacks = GetVirtualPlaybackDevices();
        var playback = virtualPlaybacks.Find(d => d.Id.Equals(virtualPlaybackDeviceId, StringComparison.OrdinalIgnoreCase));
        if (playback == null) return null;

        var virtualRecordings = GetVirtualRecordingDevices();
        if (virtualRecordings.Count == 0) return null;

        // 1. VB-CABLE: "CABLE Input" pairs with "CABLE Output"
        if (playback.Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase))
        {
            var cableRec = virtualRecordings.Find(r => r.Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase));
            if (cableRec != null) return cableRec;
        }

        // 2. Steam Streaming: "Speakers (Steam Streaming Microphone)" pairs with "Microphone (Steam Streaming Microphone)"
        if (playback.Name.Contains("Steam Streaming", StringComparison.OrdinalIgnoreCase))
        {
            var steamRec = virtualRecordings.Find(r => r.Name.Contains("Steam Streaming", StringComparison.OrdinalIgnoreCase));
            if (steamRec != null) return steamRec;
        }

        // 3. VoiceMeeter: "VoiceMeeter Input" pairs with "VoiceMeeter Output"
        if (playback.Name.Contains("VoiceMeeter", StringComparison.OrdinalIgnoreCase) ||
            playback.Name.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase))
        {
            var vmRec = virtualRecordings.Find(r => r.Name.Contains("VoiceMeeter", StringComparison.OrdinalIgnoreCase) ||
                                                    r.Name.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase));
            if (vmRec != null) return vmRec;
        }

        // 4. VAC / Virtual Audio Cable
        if (playback.Name.Contains("Line ", StringComparison.OrdinalIgnoreCase) || playback.Name.Contains("VAC", StringComparison.OrdinalIgnoreCase))
        {
            var vacRec = virtualRecordings.Find(r => r.Name.Contains("Line ", StringComparison.OrdinalIgnoreCase) || r.Name.Contains("VAC", StringComparison.OrdinalIgnoreCase));
            if (vacRec != null) return vacRec;
        }

        // Fallback: return first available virtual recording device
        return virtualRecordings[0];
    }

    /// <summary>
    /// Checks if routing system audio from source playback device to virtual playback device
    /// would cause an acoustic / digital feedback loop (i.e. capturing and playing to the exact same device).
    /// </summary>
    public static bool IsFeedbackLoop(string? playbackSourceId, string? virtualPlaybackTargetId)
    {
        if (string.IsNullOrWhiteSpace(virtualPlaybackTargetId) ||
            virtualPlaybackTargetId.Equals("off", StringComparison.OrdinalIgnoreCase) ||
            virtualPlaybackTargetId.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string? resolvedSourceId = playbackSourceId;
        if (string.IsNullOrWhiteSpace(resolvedSourceId) || resolvedSourceId.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            var def = GetDefaultPlaybackDevice();
            resolvedSourceId = def?.Id;
        }

        if (string.IsNullOrWhiteSpace(resolvedSourceId)) return false;

        return string.Equals(resolvedSourceId, virtualPlaybackTargetId, StringComparison.OrdinalIgnoreCase);
    }

    public static string GetStereoMixStatusSummary()
    {
        var recs = GetAllRecordingDevices(activeOnly: false);
        foreach (var d in recs)
        {
            if (d.Classification == AudioDeviceClassification.StereoMix)
            {
                return d.State == DeviceState.Active
                    ? $"Found & Active: '{d.Name}'"
                    : $"Found but {d.State}: '{d.Name}' (Enable in Windows Sound Control Panel)";
            }
        }
        return "Not available in sound card driver (Stereo Mix is omitted by OEM driver or not supported).";
    }

    public static string GetVirtualAudioDriverStatusSummary()
    {
        var virtualPlaybacks = GetVirtualPlaybackDevices();
        var virtualRecordings = GetVirtualRecordingDevices();

        if (virtualPlaybacks.Count > 0 && virtualRecordings.Count > 0)
        {
            return $"Compatible Virtual Audio Device Pair detected:\n" +
                   $"  • Output (Bridge Target): {virtualPlaybacks[0].Name}\n" +
                   $"  • Input (Browser Mic): {virtualRecordings[0].Name}";
        }
        else if (virtualPlaybacks.Count > 0)
        {
            return $"Virtual Playback Device detected ({virtualPlaybacks[0].Name}), but no active virtual recording device found.";
        }

        return "No active Virtual Audio Cable detected.\n" +
               "To route system audio to WebView2 / browser getUserMedia(), install a virtual audio driver like VB-Audio Virtual CABLE (free from vb-audio.com).";
    }

    private static readonly object _policyLock = new();
    public static string? OriginalDefaultRecordingDeviceId { get; private set; }
    public static string? OriginalDefaultRecordingDeviceName { get; private set; }
    public static bool HasSwitchedDefaultRecordingDevice { get; private set; }

    /// <summary>
    /// Remembers the current Windows default recording endpoint so it can be restored later.
    /// </summary>
    public static void SaveCurrentDefaultRecordingDevice()
    {
        lock (_policyLock)
        {
            if (HasSwitchedDefaultRecordingDevice) return;

            var def = GetDefaultRecordingDevice();
            if (def != null && def.Classification != AudioDeviceClassification.VirtualDevice)
            {
                OriginalDefaultRecordingDeviceId = def.Id;
                OriginalDefaultRecordingDeviceName = def.Name;
                Debug.WriteLine($"[AudioDeviceDiagnostics] Saved original default recording device: '{def.Name}' ({def.Id})");
            }
        }
    }

    /// <summary>
    /// Uses Windows IPolicyConfig COM interface to programmatically switch the system-wide
    /// default recording (microphone) endpoint to the specified device ID across all roles (Console, Multimedia, Communications).
    /// </summary>
    public static bool SetDefaultRecordingDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || deviceId == "none") return false;

        lock (_policyLock)
        {
            try
            {
                var currentDef = GetDefaultRecordingDevice();
                if (currentDef != null && string.Equals(currentDef.Id, deviceId, StringComparison.OrdinalIgnoreCase))
                {
                    HasSwitchedDefaultRecordingDevice = true;
                    return true;
                }

                if (!HasSwitchedDefaultRecordingDevice)
                {
                    SaveCurrentDefaultRecordingDevice();
                }

                var policyConfig = (IPolicyConfig)new CPolicyConfigClient();
                policyConfig.SetDefaultEndpoint(deviceId, 0); // eConsole
                policyConfig.SetDefaultEndpoint(deviceId, 1); // eMultimedia
                policyConfig.SetDefaultEndpoint(deviceId, 2); // eCommunications

                HasSwitchedDefaultRecordingDevice = true;
                Debug.WriteLine($"[AudioDeviceDiagnostics] Successfully switched Windows default recording endpoint to: {deviceId}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AudioDeviceDiagnostics] Failed to set default recording device via IPolicyConfig: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Restores the original physical Windows recording device saved before virtual routing was engaged.
    /// </summary>
    public static bool RestoreOriginalDefaultRecordingDevice()
    {
        lock (_policyLock)
        {
            if (string.IsNullOrWhiteSpace(OriginalDefaultRecordingDeviceId))
            {
                HasSwitchedDefaultRecordingDevice = false;
                return false;
            }

            try
            {
                var policyConfig = (IPolicyConfig)new CPolicyConfigClient();
                policyConfig.SetDefaultEndpoint(OriginalDefaultRecordingDeviceId, 0);
                policyConfig.SetDefaultEndpoint(OriginalDefaultRecordingDeviceId, 1);
                policyConfig.SetDefaultEndpoint(OriginalDefaultRecordingDeviceId, 2);

                Debug.WriteLine($"[AudioDeviceDiagnostics] Restored original Windows default recording device: '{OriginalDefaultRecordingDeviceName}' ({OriginalDefaultRecordingDeviceId})");
                HasSwitchedDefaultRecordingDevice = false;
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AudioDeviceDiagnostics] Failed to restore original default recording device: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Generates a comprehensive diagnostic report of all audio endpoints, routing states, and permissions.
    /// </summary>
    public static string GetAudioRoutingDiagnosticsReport(AudioCaptureManager audioManager, AppSettings settings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== MyOverlay Audio Routing & Diagnostics Report ===");
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        // 1. Windows Default Devices
        var defPlayback = GetDefaultPlaybackDevice();
        var defRecording = GetDefaultRecordingDevice();
        sb.AppendLine("--- 1. Windows Default Endpoints ---");
        sb.AppendLine($"Default Playback (Speakers/Headphones): {defPlayback?.Name ?? "None"}");
        sb.AppendLine($"Default Recording (Active Mic):        {defRecording?.Name ?? "None"}");
        sb.AppendLine($"Original Recording Device:             {OriginalDefaultRecordingDeviceName ?? "Not captured yet"}");
        sb.AppendLine($"Default Mic Switched by Overlay:       {HasSwitchedDefaultRecordingDevice}");
        sb.AppendLine();

        // 2. System Audio Capture (WASAPI Loopback)
        sb.AppendLine("--- 2. System Audio Capture (WASAPI Loopback) ---");
        sb.AppendLine($"Capture Enabled:       {settings.CaptureSystemAudio || settings.RouteToVirtualAudio}");
        sb.AppendLine($"Is Capturing:          {audioManager.SystemAudio.IsCapturing}");
        sb.AppendLine($"Active Capture Device: {audioManager.SystemAudio.CurrentDeviceName ?? "None"}");
        sb.AppendLine($"Loopback Format:       {(audioManager.SystemAudio.WaveFormat != null ? $"{audioManager.SystemAudio.WaveFormat.SampleRate}Hz, {audioManager.SystemAudio.WaveFormat.Channels}ch, {audioManager.SystemAudio.WaveFormat.BitsPerSample}bit ({audioManager.SystemAudio.WaveFormat.Encoding})" : "Not active")}");
        sb.AppendLine($"Total Callbacks:       {audioManager.SystemAudio.TotalCallbacks}");
        sb.AppendLine($"Captured Bytes:        {audioManager.SystemAudio.TotalBytesCaptured:N0} bytes");
        sb.AppendLine($"Captured Throughput:   {audioManager.SystemAudio.CapturedBytesPerSecond / 1024.0:F1} KB/s");
        sb.AppendLine();

        // 3. Virtual Audio Bridge
        sb.AppendLine("--- 3. Virtual Audio Bridge (To Virtual Microphone) ---");
        sb.AppendLine($"Route Setting:         {settings.RouteToVirtualAudio}");
        sb.AppendLine($"Bridge Active:         {audioManager.VirtualBridge.IsActive}");
        string? vId = settings.VirtualAudioPlaybackDeviceId ?? audioManager.VirtualBridge.TargetDeviceId;
        bool isVOff = string.IsNullOrWhiteSpace(vId) || vId.Equals("off", StringComparison.OrdinalIgnoreCase) || vId.Equals("none", StringComparison.OrdinalIgnoreCase);
        string targetVirtualName = isVOff ? "Off (Disabled)" : (audioManager.VirtualBridge.TargetDeviceName ?? settings.VirtualAudioPlaybackDeviceId ?? "None");
        sb.AppendLine($"Target Virtual Dev:    {targetVirtualName}");
        sb.AppendLine($"Feedback Blocked:      {audioManager.VirtualBridge.IsFeedbackBlocked}");
        sb.AppendLine($"Input Format:          {(audioManager.VirtualBridge.InputWaveFormat != null ? $"{audioManager.VirtualBridge.InputWaveFormat.SampleRate}Hz {audioManager.VirtualBridge.InputWaveFormat.Channels}ch" : "None")}");
        sb.AppendLine($"Output Format:         {(audioManager.VirtualBridge.OutputWaveFormat != null ? $"{audioManager.VirtualBridge.OutputWaveFormat.SampleRate}Hz {audioManager.VirtualBridge.OutputWaveFormat.Channels}ch" : "None")}");
        sb.AppendLine($"Bridge Input Rate:     {audioManager.VirtualBridge.InputBytesPerSecond / 1024.0:F1} KB/s");
        sb.AppendLine($"Bridge Output Rate:    {audioManager.VirtualBridge.OutputBytesPerSecond / 1024.0:F1} KB/s");
        sb.AppendLine($"Buffer Queue:          {audioManager.VirtualBridge.CurrentBufferedBytes:N0} bytes ({audioManager.VirtualBridge.BufferLatencyMs:F1} ms)");
        sb.AppendLine($"Max Buffer Capacity:   {audioManager.VirtualBridge.BufferDurationMs:F0} ms");
        sb.AppendLine($"Starvation / Drops:    {audioManager.VirtualBridge.StarvationCount} starvations, {audioManager.VirtualBridge.OverflowCount} overflows");
        sb.AppendLine();

        // 4. Paired Browser Recording Endpoint
        var pairedCapture = isVOff ? null : FindPairedVirtualRecordingDevice(vId);
        sb.AppendLine("--- 4. Paired Browser Recording Endpoint ---");
        sb.AppendLine($"Paired Virtual Mic:    {(isVOff ? "Off (Output Bus A Disabled)" : (pairedCapture?.Name ?? "Not detected"))}");
        bool isDefaultMic = pairedCapture != null && defRecording != null && pairedCapture.Id.Equals(defRecording.Id, StringComparison.OrdinalIgnoreCase);
        sb.AppendLine($"Is Windows Default:    {(isVOff ? "N/A (Output Disabled)" : (isDefaultMic ? "YES ✓ (Chromium getUserMedia will automatically pick this)" : "NO ⚠️ (Must be Default for ChatGPT/browser to hear system audio)"))}");
        sb.AppendLine();

        // 5. Feedback Check
        bool feedbackLoop = !isVOff && IsFeedbackLoop(settings.SystemAudioDeviceId, settings.VirtualAudioPlaybackDeviceId);
        sb.AppendLine("--- 5. Audio Feedback Loop Prevention ---");
        sb.AppendLine($"Feedback Detected:     {feedbackLoop}");
        if (feedbackLoop)
        {
            sb.AppendLine("⚠️ WARNING: System audio capture source and virtual target are identical! Bridging is blocked to prevent echo.");
        }
        else
        {
            sb.AppendLine("Status: Safe (Source and target endpoints are distinct).");
        }
        sb.AppendLine();

        // 6. WebView2 Permissions
        sb.AppendLine("--- 6. WebView2 Browser Microphone Permissions ---");
        sb.AppendLine($"Auto-Grant Mic:        {WebView2AudioPermissionTracker.AutoGrantMicrophone}");
        sb.AppendLine($"Total Requests:        {WebView2AudioPermissionTracker.TotalRequests}");
        sb.AppendLine($"Last Request:          {WebView2AudioPermissionTracker.GetStatusDescription()}");
        sb.AppendLine();

        // 7. Detected Virtual Devices
        var vPlaybacks = GetVirtualPlaybackDevices();
        var vRecordings = GetVirtualRecordingDevices();
        sb.AppendLine($"--- 7. Detected Virtual Devices ({vPlaybacks.Count} Playback, {vRecordings.Count} Recording) ---");
        sb.AppendLine("Playback Devices (Bridge Targets):");
        if (vPlaybacks.Count == 0) sb.AppendLine("  (None found)");
        foreach (var vp in vPlaybacks) sb.AppendLine($"  • {vp.Name} (ID: {vp.Id})");

        sb.AppendLine("Recording Devices (Browser Input):");
        if (vRecordings.Count == 0) sb.AppendLine("  (None found)");
        foreach (var vr in vRecordings) sb.AppendLine($"  • {vr.Name} (ID: {vr.Id})");

        sb.AppendLine();
        sb.AppendLine("--- 8. Stereo Mix Status ---");
        sb.AppendLine(GetStereoMixStatusSummary());

        return sb.ToString();
    }

    /// <summary>
    /// Opens the classic Windows Sound Control Panel directly to the Recording tab.
    /// </summary>
    public static void OpenWindowsSoundRecordingControlPanel()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "control",
                Arguments = "mmsys.cpl,,1",
                UseShellExecute = true
            });
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "ms-settings:sound",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AudioDeviceDiagnostics] Failed to open Sound settings: {ex.Message}");
            }
        }
    }
}

[ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
internal class CPolicyConfigClient { }

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
internal interface IPolicyConfig
{
    void GetMixFormat(string pszDeviceName, out IntPtr ppFormat);
    void GetDeviceFormat(string pszDeviceName, int bDefault, out IntPtr ppFormat);
    void ResetDeviceFormat(string pszDeviceName);
    void SetDeviceFormat(string pszDeviceName, IntPtr pEndpointFormat, IntPtr MixFormat);
    void GetProcessingPeriod(string pszDeviceName, int bDefault, out long pmftDefaultPeriod, out long pmftMinimumPeriod);
    void SetProcessingPeriod(string pszDeviceName, long pmftPeriod);
    void GetShareMode(string pszDeviceName, out int pMode);
    void SetShareMode(string pszDeviceName, int mode);
    void GetPropertyValue(string pszDeviceName, bool bDefault, IntPtr pKey, IntPtr pValue);
    void SetPropertyValue(string pszDeviceName, bool bDefault, IntPtr pKey, IntPtr pValue);
    void SetDefaultEndpoint(string wszDeviceId, int eRole);
    void SetEndpointVisibility(string pszDeviceName, int bVisible);
}

