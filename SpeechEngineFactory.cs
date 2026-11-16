using System;

namespace MyOverlay;

public static class SpeechEngineFactory
{
    public const string EngineWindows = "WindowsSpeech";
    public const string EngineOpenAI = "WhisperOpenAI";
    public const string EngineGroq = "WhisperGroq";
    public const string EngineCustom = "CustomEndpoint";

    public static ISpeechToTextEngine CreateEngine(AppSettings settings)
    {
        string engineType = settings.SpeechToTextEngine ?? EngineWindows;

        if (engineType.Equals(EngineOpenAI, StringComparison.OrdinalIgnoreCase))
        {
            string endpoint = string.IsNullOrWhiteSpace(settings.SpeechToTextEndpoint)
                ? "https://api.openai.com/v1/audio/transcriptions"
                : settings.SpeechToTextEndpoint;

            string model = string.IsNullOrWhiteSpace(settings.SpeechToTextModel)
                ? "whisper-1"
                : settings.SpeechToTextModel;

            return new WhisperApiSpeechEngine(
                endpoint: endpoint,
                apiKey: settings.SpeechToTextApiKey,
                model: model,
                language: settings.SpeechToTextLanguage,
                displayName: "OpenAI Whisper API"
            );
        }
        else if (engineType.Equals(EngineGroq, StringComparison.OrdinalIgnoreCase))
        {
            string endpoint = "https://api.groq.com/openai/v1/audio/transcriptions";
            string model = string.IsNullOrWhiteSpace(settings.SpeechToTextModel)
                ? "whisper-large-v3-turbo"
                : settings.SpeechToTextModel;

            return new WhisperApiSpeechEngine(
                endpoint: endpoint,
                apiKey: settings.SpeechToTextApiKey,
                model: model,
                language: settings.SpeechToTextLanguage,
                displayName: "Groq Whisper API"
            );
        }
        else if (engineType.Equals(EngineCustom, StringComparison.OrdinalIgnoreCase))
        {
            return new WhisperApiSpeechEngine(
                endpoint: settings.SpeechToTextEndpoint,
                apiKey: settings.SpeechToTextApiKey,
                model: settings.SpeechToTextModel,
                language: settings.SpeechToTextLanguage,
                displayName: "Custom Whisper Endpoint"
            );
        }
        else
        {
            // Default to Windows Built-in Speech Recognizer (Offline, Free)
            return new WindowsSpeechEngine(settings.SpeechToTextLanguage);
        }
    }
}
