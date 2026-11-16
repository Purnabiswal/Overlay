using System;
using System.Collections.Generic;
using Microsoft.Web.WebView2.Core;

namespace MyOverlay;

public class WebView2PermissionRecord
{
    public string Uri { get; set; } = string.Empty;
    public CoreWebView2PermissionKind Kind { get; set; }
    public CoreWebView2PermissionState State { get; set; }
    public DateTime Timestamp { get; set; }

    public override string ToString()
    {
        return $"[{Timestamp:HH:mm:ss}] {Kind}: {State} for {(string.IsNullOrWhiteSpace(Uri) ? "Unknown origin" : Uri)}";
    }
}

/// <summary>
/// Monitors, logs, and controls WebView2 microphone and media permission requests
/// for diagnostic and audio routing reporting.
/// </summary>
public static class WebView2AudioPermissionTracker
{
    private static readonly object _syncLock = new();
    private static readonly List<WebView2PermissionRecord> _recentRecords = new();
    private static WebView2PermissionRecord? _lastRecord;
    private static bool _autoGrantMicrophone = true;
    private static int _totalRequests;

    public static bool AutoGrantMicrophone
    {
        get
        {
            lock (_syncLock) return _autoGrantMicrophone;
        }
        set
        {
            lock (_syncLock) _autoGrantMicrophone = value;
        }
    }

    public static WebView2PermissionRecord? LastRecord
    {
        get
        {
            lock (_syncLock) return _lastRecord;
        }
    }

    public static int TotalRequests
    {
        get
        {
            lock (_syncLock) return _totalRequests;
        }
    }

    public static IReadOnlyList<WebView2PermissionRecord> RecentRecords
    {
        get
        {
            lock (_syncLock) return _recentRecords.ToArray();
        }
    }

    public static event EventHandler<WebView2PermissionRecord>? PermissionEventLogged;

    public static void RecordRequest(string uri, CoreWebView2PermissionKind kind, CoreWebView2PermissionState state)
    {
        var record = new WebView2PermissionRecord
        {
            Uri = uri,
            Kind = kind,
            State = state,
            Timestamp = DateTime.Now
        };

        lock (_syncLock)
        {
            _lastRecord = record;
            _totalRequests++;
            _recentRecords.Add(record);
            if (_recentRecords.Count > 50)
            {
                _recentRecords.RemoveAt(0);
            }
        }

        try
        {
            PermissionEventLogged?.Invoke(null, record);
        }
        catch { }
    }

    public static string GetStatusDescription()
    {
        lock (_syncLock)
        {
            string grantMode = _autoGrantMicrophone ? "Auto-grant: Enabled" : "Auto-grant: Disabled (Prompt)";
            if (_lastRecord == null)
            {
                return $"No microphone requests intercepted yet. ({grantMode})";
            }

            string origin = string.IsNullOrWhiteSpace(_lastRecord.Uri) ? "Unknown Origin" : _lastRecord.Uri;
            return $"[{_lastRecord.Timestamp:HH:mm:ss}] {_lastRecord.State} for {origin} ({grantMode})";
        }
    }

    public static void Clear()
    {
        lock (_syncLock)
        {
            _lastRecord = null;
            _totalRequests = 0;
            _recentRecords.Clear();
        }
    }
}
