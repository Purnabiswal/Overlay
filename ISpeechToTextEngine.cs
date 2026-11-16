using System;
using System.Threading;
using System.Threading.Tasks;

namespace MyOverlay;

/// <summary>
/// Service interface abstracting speech-to-text engines (e.g. OpenAI Whisper API, Groq Whisper API, Windows Built-in Speech).
/// Keeps external API details, credentials, and local recognizers isolated behind a unified contract.
/// </summary>
public interface ISpeechToTextEngine : IDisposable
{
    /// <summary>
    /// Friendly display name of this speech-to-text provider.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Indicates whether the engine is properly configured (e.g. required API key provided or local recognizer installed).
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Human-readable explanation if configuration is incomplete or missing.
    /// </summary>
    string ConfigurationErrorDescription { get; }

    /// <summary>
    /// Transcribes a 16kHz 16-bit mono RIFF WAV audio buffer into text.
    /// </summary>
    /// <param name="wavBytes">Standard RIFF WAV byte array.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Recognized text or null/empty if no speech detected.</returns>
    Task<string?> TranscribeWavAsync(byte[] wavBytes, CancellationToken cancellationToken = default);
}
