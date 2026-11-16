using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace MyOverlay;

/// <summary>
/// Dedicated Microphone Audio capture component.
/// Kept completely separate from SystemAudioCapture (WASAPI loopback).
/// Automatically targets physical microphones (skipping virtual loopback devices to prevent feedback)
/// and supports hot-plugging for wired, USB, and Bluetooth headsets.
/// </summary>
public sealed class MicrophoneAudioCapture : IDisposable, IMMNotificationClient
{
    private readonly object _syncLock = new();
    private MMDeviceEnumerator? _deviceEnumerator;
    private WasapiCapture? _capture;
    private string? _selectedDeviceId; // null or "default" = physical Windows microphone
    private bool _isCapturing;
    private bool _isDisposed;
    private bool _isReconnecting;

    /// <summary>
    /// Event raised whenever microphone PCM audio data is recorded.
    /// </summary>
    public event EventHandler<AudioDataEventArgs>? AudioData;

    /// <summary>
    /// Event raised with normalized peak level [0.0 - 1.0] for real-time microphone VU meter.
    /// </summary>
    public event EventHandler<float>? AudioLevelChanged;

    /// <summary>
    /// Event raised when microphone capture status changes.
    /// </summary>
    public event EventHandler<string>? StatusChanged;

    /// <summary>
    /// Event raised when an error occurs during microphone capture.
    /// </summary>
    public event EventHandler<Exception>? CaptureError;

    /// <summary>
    /// Event raised when microphone devices are added, removed, or changed.
    /// </summary>
    public event EventHandler? DeviceChanged;

    /// <summary>
    /// Indicates whether microphone audio capture is currently running.
    /// </summary>
    public bool IsCapturing
    {
        get
        {
            lock (_syncLock)
            {
                return _isCapturing;
            }
        }
    }

    /// <summary>
    /// Returns the ID of the microphone device currently being captured.
    /// </summary>
    public string? CurrentDeviceId { get; private set; }

    /// <summary>
    /// Returns the friendly name of the microphone device currently being captured.
    /// </summary>
    public string? CurrentDeviceName { get; private set; }

    /// <summary>
    /// Gets the PCM WaveFormat of the active microphone capture stream.
    /// </summary>
    public WaveFormat? WaveFormat
    {
        get
        {
            lock (_syncLock)
            {
                return _capture?.WaveFormat;
            }
        }
    }

    public bool UseDefaultDevice =>
        string.IsNullOrEmpty(_selectedDeviceId) ||
        _selectedDeviceId.Equals("default", StringComparison.OrdinalIgnoreCase);

    public string? SelectedDeviceId
    {
        get => _selectedDeviceId;
        set => SetDevice(value);
    }

    public MicrophoneAudioCapture(string? deviceId = null)
    {
        _selectedDeviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;
        try
        {
            _deviceEnumerator = new MMDeviceEnumerator();
            _deviceEnumerator.RegisterEndpointNotificationCallback(this);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MicrophoneAudioCapture] Failed to register MMDevice notification callback: {ex.Message}");
        }
    }

    /// <summary>
    /// Enumerates all active Windows audio recording/microphone (capture) endpoints.
    /// </summary>
    public static List<AudioDeviceInfo> GetCaptureDevices()
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

            var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            foreach (var ep in endpoints)
            {
                try
                {
                    result.Add(new AudioDeviceInfo
                    {
                        Id = ep.ID,
                        Name = ep.FriendlyName,
                        IsDefault = (defaultId != null && ep.ID.Equals(defaultId, StringComparison.OrdinalIgnoreCase)),
                        IsPlayback = false,
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
            Debug.WriteLine($"[MicrophoneAudioCapture] Error enumerating capture devices: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Resolves the default physical microphone, explicitly ignoring virtual recording endpoints
    /// so the microphone input never accidentally records from a virtual cable / feedback loop.
    /// </summary>
    public static MMDevice? GetDefaultPhysicalMicrophone(MMDeviceEnumerator enumerator)
    {
        try
        {
            var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            if (def != null && AudioDeviceInfo.Classify(def.FriendlyName, false) == AudioDeviceClassification.PhysicalMicrophone)
            {
                return def;
            }
        }
        catch { }

        try
        {
            var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            foreach (var ep in endpoints)
            {
                if (AudioDeviceInfo.Classify(ep.FriendlyName, false) == AudioDeviceClassification.PhysicalMicrophone)
                {
                    return ep;
                }
            }
        }
        catch { }

        try
        {
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Configures the microphone device to capture from.
    /// </summary>
    public void SetDevice(string? deviceId)
    {
        string? newDeviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;

        lock (_syncLock)
        {
            if (string.Equals(_selectedDeviceId, newDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _selectedDeviceId = newDeviceId;
        }

        if (IsCapturing)
        {
            Task.Run(RestartCaptureInternal);
        }
    }

    /// <summary>
    /// Starts recording from the configured microphone.
    /// </summary>
    public void Start()
    {
        lock (_syncLock)
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(MicrophoneAudioCapture));
            }

            if (_isCapturing)
            {
                return;
            }

            StartInternal();
        }
    }

    public Task StartAsync() => Task.Run(Start);

    /// <summary>
    /// Stops recording from the microphone.
    /// </summary>
    public void Stop()
    {
        lock (_syncLock)
        {
            if (!_isCapturing && _capture == null)
            {
                return;
            }

            StopInternal();
        }
    }

    public Task StopAsync() => Task.Run(Stop);

    private void StartInternal()
    {
        try
        {
            if (_deviceEnumerator == null)
            {
                _deviceEnumerator = new MMDeviceEnumerator();
                try { _deviceEnumerator.RegisterEndpointNotificationCallback(this); } catch { }
            }

            MMDevice? targetDevice = null;

            if (UseDefaultDevice)
            {
                targetDevice = GetDefaultPhysicalMicrophone(_deviceEnumerator);
            }
            else
            {
                try
                {
                    targetDevice = _deviceEnumerator.GetDevice(_selectedDeviceId!);
                    if (targetDevice.State != DeviceState.Active)
                    {
                        NotifyStatus($"Selected microphone '{targetDevice.FriendlyName}' is not active. Falling back to physical default.");
                        targetDevice = GetDefaultPhysicalMicrophone(_deviceEnumerator);
                    }
                }
                catch (Exception ex)
                {
                    NotifyStatus($"Failed to open selected microphone ({ex.Message}). Falling back to default.");
                    targetDevice = GetDefaultPhysicalMicrophone(_deviceEnumerator);
                }
            }

            if (targetDevice == null)
            {
                NotifyError(new InvalidOperationException("No microphone device could be resolved."));
                return;
            }

            CurrentDeviceId = targetDevice.ID;
            CurrentDeviceName = targetDevice.FriendlyName;

            _capture = new WasapiCapture(targetDevice);
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;

            _capture.StartRecording();
            _isCapturing = true;

            NotifyStatus($"Capturing microphone audio from {CurrentDeviceName} ({_capture.WaveFormat.SampleRate} Hz, {_capture.WaveFormat.Channels} ch)");
            DeviceChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _isCapturing = false;
            CurrentDeviceId = null;
            CurrentDeviceName = null;
            NotifyError(new InvalidOperationException($"Failed to start microphone capture: {ex.Message}", ex));
        }
    }

    private void StopInternal()
    {
        _isCapturing = false;

        try
        {
            if (_capture != null)
            {
                _capture.DataAvailable -= OnDataAvailable;
                _capture.RecordingStopped -= OnRecordingStopped;

                try
                {
                    _capture.StopRecording();
                }
                catch
                {
                }

                _capture.Dispose();
                _capture = null;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MicrophoneAudioCapture] Error stopping capture: {ex.Message}");
        }

        CurrentDeviceId = null;
        CurrentDeviceName = null;
        AudioLevelChanged?.Invoke(this, 0.0f);
        NotifyStatus("Microphone capture stopped.");
    }

    private void RestartCaptureInternal()
    {
        lock (_syncLock)
        {
            if (_isDisposed) return;
            StopInternal();
            StartInternal();
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0 || _isDisposed) return;

        try
        {
            var format = _capture?.WaveFormat;
            float peak = SystemAudioCapture.CalculatePeak(e.Buffer, e.BytesRecorded, format);

            AudioLevelChanged?.Invoke(this, peak);

            if (format != null)
            {
                var eventArgs = new AudioDataEventArgs(e.Buffer, e.BytesRecorded, format, peak, AudioSourceType.Microphone);
                AudioData?.Invoke(this, eventArgs);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MicrophoneAudioCapture] Error processing buffer: {ex.Message}");
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        bool wasCapturing;
        lock (_syncLock)
        {
            wasCapturing = _isCapturing;
            _isCapturing = false;
        }

        AudioLevelChanged?.Invoke(this, 0.0f);

        if (e.Exception != null)
        {
            if (wasCapturing && !_isDisposed)
            {
                NotifyStatus($"Microphone disconnected or reset: {e.Exception.Message}. Attempting reconnect...");
                ScheduleAutoReconnect();
            }
            else
            {
                NotifyError(e.Exception);
            }
        }
    }

    private void ScheduleAutoReconnect()
    {
        if (_isReconnecting || _isDisposed) return;
        _isReconnecting = true;

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1000);
                lock (_syncLock)
                {
                    if (_isDisposed) return;
                    StopInternal();
                    StartInternal();
                }
            }
            catch (Exception ex)
            {
                NotifyError(ex);
            }
            finally
            {
                _isReconnecting = false;
            }
        });
    }

    private void NotifyStatus(string message)
    {
        Debug.WriteLine($"[MicrophoneAudioCapture] {message}");
        try
        {
            StatusChanged?.Invoke(this, message);
        }
        catch { }
    }

    private void NotifyError(Exception ex)
    {
        Debug.WriteLine($"[MicrophoneAudioCapture ERROR] {ex}");
        try
        {
            CaptureError?.Invoke(this, ex);
        }
        catch { }
    }

    #region IMMNotificationClient Implementation (Microphone Hot-Plugging)

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow != DataFlow.Capture) return;
        DeviceChanged?.Invoke(this, EventArgs.Empty);

        if (UseDefaultDevice && IsCapturing)
        {
            // Reconnect if default physical mic changed
            ScheduleAutoReconnect();
        }
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
        DeviceChanged?.Invoke(this, EventArgs.Empty);

        if (string.Equals(CurrentDeviceId, deviceId, StringComparison.OrdinalIgnoreCase) &&
            newState != DeviceState.Active &&
            IsCapturing)
        {
            NotifyStatus($"Microphone device state changed to {newState}. Reconnecting...");
            ScheduleAutoReconnect();
        }
    }

    public void OnDeviceAdded(string pwstrDeviceId)
    {
        DeviceChanged?.Invoke(this, EventArgs.Empty);
    }

    public void OnDeviceRemoved(string pwstrDeviceId)
    {
        DeviceChanged?.Invoke(this, EventArgs.Empty);
    }

    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    #endregion

    public void Dispose()
    {
        lock (_syncLock)
        {
            if (_isDisposed) return;
            _isDisposed = true;

            StopInternal();

            try
            {
                if (_deviceEnumerator != null)
                {
                    _deviceEnumerator.UnregisterEndpointNotificationCallback(this);
                    _deviceEnumerator.Dispose();
                    _deviceEnumerator = null;
                }
            }
            catch { }
        }

        GC.SuppressFinalize(this);
    }
}
