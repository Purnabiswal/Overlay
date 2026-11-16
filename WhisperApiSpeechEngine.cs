using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MyOverlay;

/// <summary>
/// Speech-to-text engine implementing OpenAI-compatible Whisper API.
/// Supports OpenAI Whisper (whisper-1), Groq Whisper (whisper-large-v3 / whisper-large-v3-turbo),
/// and custom local OpenAI-compatible endpoints (Faster-Whisper, whisper.cpp, Ollama).
/// </summary>
public sealed class WhisperApiSpeechEngine : ISpeechToTextEngine
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _endpoint;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string? _language;
    private readonly string _name;

    public string Name => _name;

    public bool IsConfigured
    {
        get
        {
            // If endpoint is a remote cloud service (e.g. api.openai.com or api.groq.com), an API key is mandatory.
            bool isLocal = _endpoint.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
                           _endpoint.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase);
            return isLocal || !string.IsNullOrWhiteSpace(_apiKey);
        }
    }

    public string ConfigurationErrorDescription
    {
        get
        {
            if (IsConfigured) return string.Empty;
            return "API Key is required for this Whisper endpoint. Please enter your API key in Settings (Ctrl+Alt+S).";
        }
    }

    public WhisperApiSpeechEngine(
        string endpoint = "https://api.openai.com/v1/audio/transcriptions",
        string apiKey = "",
        string model = "whisper-1",
        string? language = "en",
        string? displayName = null,
        HttpClient? httpClient = null)
    {
        _endpoint = string.IsNullOrWhiteSpace(endpoint) ? "https://api.openai.com/v1/audio/transcriptions" : endpoint.Trim();
        _apiKey = apiKey?.Trim() ?? string.Empty;
        _model = string.IsNullOrWhiteSpace(model) ? "whisper-1" : model.Trim();
        _language = string.IsNullOrWhiteSpace(language) ? null : language.Trim();

        if (displayName != null)
        {
            _name = displayName;
        }
        else if (_endpoint.Contains("groq.com", StringComparison.OrdinalIgnoreCase))
        {
            _name = "Groq Whisper API";
        }
        else if (_endpoint.Contains("openai.com", StringComparison.OrdinalIgnoreCase))
        {
            _name = "OpenAI Whisper API";
        }
        else
        {
            _name = "Custom Whisper API";
        }

        if (httpClient != null)
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            _ownsHttpClient = true;
        }
    }

    public async Task<string?> TranscribeWavAsync(byte[] wavBytes, CancellationToken cancellationToken = default)
    {
        if (wavBytes == null || wavBytes.Length <= 44)
        {
            return null;
        }

        if (!IsConfigured)
        {
            throw new InvalidOperationException($"Cannot transcribe audio: {ConfigurationErrorDescription}");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);

        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }

        using var multipart = new MultipartFormDataContent();

        // 1. Audio file content
        var audioContent = new ByteArrayContent(wavBytes);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        multipart.Add(audioContent, "file", "audio.wav");

        // 2. Model parameter
        multipart.Add(new StringContent(_model), "model");

        // 3. Response format
        multipart.Add(new StringContent("json"), "response_format");

        // 4. Language (optional)
        if (!string.IsNullOrWhiteSpace(_language) && !_language.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            multipart.Add(new StringContent(_language), "language");
        }

        request.Content = multipart;

        try
        {
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                string errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException($"Whisper API request failed (HTTP {(int)response.StatusCode} {response.StatusCode}): {errorBody}");
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken);

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("text", out var textProp))
            {
                string? text = textProp.GetString();
                return CleanTranscribedText(text);
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WhisperApiSpeechEngine ERROR] {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Filters out common speech model artifacts, hallucinations, and empty transcripts.
    /// </summary>
    private static string? CleanTranscribedText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        string trimmed = text.Trim();

        // Filter standard Whisper hallucination tokens when audio is near-silent
        if (trimmed.Equals("[BLANK_AUDIO]", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("[silence]", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("[music]", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("[applause]", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("Thank you for watching.", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("Thanks for watching!", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("Subtitles by the Amara.org community", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Return trimmed recognized text
        return trimmed;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
