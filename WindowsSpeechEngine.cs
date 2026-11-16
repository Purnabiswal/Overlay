using System;
using System.IO;
using System.Linq;
using System.Speech.Recognition;
using System.Threading;
using System.Threading.Tasks;

namespace MyOverlay;

/// <summary>
/// Offline Speech-to-Text engine using Windows Desktop Speech Recognition (System.Speech.Recognition).
/// Requires zero external API keys or network connection and utilizes Windows' built-in speech recognizer.
/// </summary>
public sealed class WindowsSpeechEngine : ISpeechToTextEngine
{
    private readonly object _syncLock = new();
    private SpeechRecognitionEngine? _engine;
    private readonly RecognizerInfo? _recognizerInfo;
    private bool _isDisposed;

    public string Name => $"Windows Built-in Speech ({_recognizerInfo?.Culture.Name ?? "None"})";

    public bool IsConfigured => _recognizerInfo != null && _engine != null;

    public string ConfigurationErrorDescription => _recognizerInfo == null
        ? "No Windows Speech Recognizer is installed on this system. Please install the Windows English speech language pack or use Whisper API."
        : (_engine == null ? "Failed to initialize Windows Speech Recognition Engine." : string.Empty);

    public WindowsSpeechEngine(string? preferredCulture = null)
    {
        try
        {
            var installed = SpeechRecognitionEngine.InstalledRecognizers();
            if (installed != null && installed.Count > 0)
            {
                if (!string.IsNullOrWhiteSpace(preferredCulture))
                {
                    _recognizerInfo = installed.FirstOrDefault(r => r.Culture.Name.Equals(preferredCulture, StringComparison.OrdinalIgnoreCase))
                                   ?? installed.FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName.Equals(preferredCulture, StringComparison.OrdinalIgnoreCase));
                }

                _recognizerInfo ??= installed.FirstOrDefault(r => r.Culture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                                 ?? installed[0];

                if (_recognizerInfo != null)
                {
                    _engine = new SpeechRecognitionEngine(_recognizerInfo);
                    _engine.LoadGrammar(new DictationGrammar());
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WindowsSpeechEngine] Initialization failed: {ex.Message}");
            _engine?.Dispose();
            _engine = null;
        }
    }

    public Task<string?> TranscribeWavAsync(byte[] wavBytes, CancellationToken cancellationToken = default)
    {
        if (wavBytes == null || wavBytes.Length <= 44)
        {
            return Task.FromResult<string?>(null);
        }

        if (!IsConfigured || _engine == null)
        {
            throw new InvalidOperationException($"Cannot transcribe audio: {ConfigurationErrorDescription}");
        }

        return Task.Run(() =>
        {
            lock (_syncLock)
            {
                if (_isDisposed || _engine == null) return null;

                try
                {
                    using var ms = new MemoryStream(wavBytes);
                    _engine.SetInputToWaveStream(ms);

                    // Recognize speech synchronously from the WAV stream
                    RecognitionResult? result = _engine.Recognize(TimeSpan.FromSeconds(8));

                    if (result != null && !string.IsNullOrWhiteSpace(result.Text))
                    {
                        return result.Text.Trim();
                    }

                    return null;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WindowsSpeechEngine Error] {ex.GetType().Name}: {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"[WindowsSpeechEngine] Recognition error: {ex.Message}");
                    return null;
                }
                finally
                {
                    try
                    {
                        _engine?.SetInputToNull();
                    }
                    catch { }
                }
            }
        }, cancellationToken);
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            if (_isDisposed) return;
            _isDisposed = true;

            try
            {
                if (_engine != null)
                {
                    _engine.Dispose();
                    _engine = null;
                }
            }
            catch { }
        }
    }
}
