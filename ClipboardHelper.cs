using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace MyOverlay;

/// <summary>
/// Thread-safe WinForms clipboard helper.
/// Handles STA thread requirements, retries for clipboard lock contention, and safe error reporting.
/// </summary>
public static class ClipboardHelper
{
    /// <summary>Places an in-memory bitmap on the clipboard, retrying transient clipboard locks.</summary>
    public static bool TrySetImage(Image image, out string? errorMessage)
    {
        errorMessage = null;
        if (image == null) { errorMessage = "Cannot copy a null image to the clipboard."; return false; }
        try
        {
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    // Clipboard owns the cloned bitmap after SetDataObject returns.
                    using var copy = new Bitmap(image);
                    Clipboard.SetDataObject(copy, true, 5, 100);
                    return true;
                }
                catch (ExternalException ex) { errorMessage = ex.Message; Thread.Sleep(75); }
            }
        }
        catch (Exception ex) { errorMessage = ex.Message; }
        return false;
    }

    /// <summary>
    /// Attempts to copy text to the Windows clipboard safely.
    /// Guarantees execution on an STA thread with automatic retries for locked clipboards.
    /// </summary>
    public static bool TrySetText(string text, out string? errorMessage)
    {
        errorMessage = null;
        if (text == null)
        {
            errorMessage = "Cannot copy null text to clipboard.";
            return false;
        }

        try
        {
            // If the current thread is already an STA thread (e.g. WinForms UI thread)
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            {
                return SetClipboardWithRetry(text, out errorMessage);
            }

            // If called from a non-STA background thread, run on a dedicated STA thread
            bool success = false;
            string? threadError = null;

            var staThread = new Thread(() =>
            {
                success = SetClipboardWithRetry(text, out threadError);
            });
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.IsBackground = true;
            staThread.Start();

            bool completed = staThread.Join(2000);
            if (!completed)
            {
                errorMessage = "Clipboard copy timed out after 2000ms.";
                return false;
            }

            errorMessage = threadError;
            return success;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private static bool SetClipboardWithRetry(string text, out string? errorMessage)
    {
        errorMessage = null;
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                // Clipboard.SetDataObject with persistent copy flag (true) and 5 retries / 100ms delay
                Clipboard.SetDataObject(text, true, 5, 100);
                return true;
            }
            catch (ExternalException ex)
            {
                errorMessage = ex.Message;
                // If persistent copy fails due to clipboard lock contention, attempt standard SetText on late attempts
                if (attempt >= 4)
                {
                    try
                    {
                        Clipboard.SetText(text);
                        return true;
                    }
                    catch { }
                }
                Thread.Sleep(50);
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                break;
            }
        }

        return false;
    }

    /// <summary>
    /// Attempts to read text from the Windows clipboard safely.
    /// Guarantees execution on an STA thread.
    /// </summary>
    public static bool TryGetText(out string text, out string? errorMessage)
    {
        errorMessage = null;
        text = string.Empty;

        try
        {
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            {
                text = Clipboard.GetText();
                return true;
            }

            string result = string.Empty;
            string? threadError = null;
            bool success = false;

            var staThread = new Thread(() =>
            {
                try
                {
                    result = Clipboard.GetText();
                    success = true;
                }
                catch (Exception ex)
                {
                    threadError = ex.Message;
                }
            });
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.IsBackground = true;
            staThread.Start();

            bool completed = staThread.Join(2000);
            if (!completed)
            {
                errorMessage = "Clipboard read timed out after 2000ms.";
                return false;
            }

            errorMessage = threadError;
            text = result;
            return success;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }
}
