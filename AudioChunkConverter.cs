using System;
using System.IO;
using NAudio.Wave;

namespace MyOverlay;

/// <summary>
/// Audio conversion utility that normalizes audio buffers from system capture (typically 44.1kHz or 48kHz, stereo, 32-bit float)
/// into standard 16kHz 16-bit mono PCM WAV format for speech-to-text engines (Whisper API and Windows Speech Recognition).
/// </summary>
public static class AudioChunkConverter
{
    public const int TargetSampleRate = 16000;
    public const int TargetBitsPerSample = 16;
    public const int TargetChannels = 1;

    /// <summary>
    /// Decodes raw PCM or IEEE float audio bytes into normalized mono float samples [-1.0 to 1.0].
    /// </summary>
    public static float[] DecodeToMonoFloatSamples(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        if (buffer == null || bytesRecorded <= 0 || format == null)
        {
            return Array.Empty<float>();
        }

        int channels = Math.Max(1, format.Channels);

        if (format.Encoding == WaveFormatEncoding.IeeeFloat || format.BitsPerSample == 32)
        {
            int totalSamples = bytesRecorded / 4;
            int frameCount = totalSamples / channels;
            float[] monoSamples = new float[frameCount];

            for (int frame = 0; frame < frameCount; frame++)
            {
                float sum = 0f;
                for (int ch = 0; ch < channels; ch++)
                {
                    int byteOffset = (frame * channels + ch) * 4;
                    if (byteOffset + 4 <= bytesRecorded)
                    {
                        float sample = BitConverter.ToSingle(buffer, byteOffset);
                        if (!float.IsNaN(sample) && !float.IsInfinity(sample))
                        {
                            sum += sample;
                        }
                    }
                }
                monoSamples[frame] = sum / channels;
            }
            return monoSamples;
        }
        else if (format.BitsPerSample == 16)
        {
            int totalSamples = bytesRecorded / 2;
            int frameCount = totalSamples / channels;
            float[] monoSamples = new float[frameCount];

            for (int frame = 0; frame < frameCount; frame++)
            {
                float sum = 0f;
                for (int ch = 0; ch < channels; ch++)
                {
                    int byteOffset = (frame * channels + ch) * 2;
                    if (byteOffset + 2 <= bytesRecorded)
                    {
                        short sample = BitConverter.ToInt16(buffer, byteOffset);
                        sum += sample / 32768.0f;
                    }
                }
                monoSamples[frame] = sum / channels;
            }
            return monoSamples;
        }
        else if (format.BitsPerSample == 24)
        {
            int totalSamples = bytesRecorded / 3;
            int frameCount = totalSamples / channels;
            float[] monoSamples = new float[frameCount];

            for (int frame = 0; frame < frameCount; frame++)
            {
                float sum = 0f;
                for (int ch = 0; ch < channels; ch++)
                {
                    int byteOffset = (frame * channels + ch) * 3;
                    if (byteOffset + 3 <= bytesRecorded)
                    {
                        int sample = (buffer[byteOffset] << 8) | (buffer[byteOffset + 1] << 16) | (buffer[byteOffset + 2] << 24);
                        sum += sample / 2147483648.0f;
                    }
                }
                monoSamples[frame] = sum / channels;
            }
            return monoSamples;
        }

        return Array.Empty<float>();
    }

    /// <summary>
    /// Resamples mono float samples to the target sample rate (16,000 Hz) using linear interpolation.
    /// </summary>
    public static float[] Resample(float[] input, int inputSampleRate, int targetSampleRate = TargetSampleRate)
    {
        if (input == null || input.Length == 0)
        {
            return Array.Empty<float>();
        }

        if (inputSampleRate <= 0 || inputSampleRate == targetSampleRate)
        {
            return input;
        }

        double ratio = (double)targetSampleRate / inputSampleRate;
        int outputLength = Math.Max(1, (int)Math.Round(input.Length * ratio));
        float[] output = new float[outputLength];

        for (int i = 0; i < outputLength; i++)
        {
            double sourcePos = i / ratio;
            int index = (int)sourcePos;
            double frac = sourcePos - index;

            int i0 = Math.Clamp(index, 0, input.Length - 1);
            int i1 = Math.Clamp(index + 1, 0, input.Length - 1);

            output[i] = (float)(input[i0] + frac * (input[i1] - input[i0]));
        }

        return output;
    }

    /// <summary>
    /// Packages mono float samples into standard 16kHz 16-bit Mono RIFF WAV byte array.
    /// </summary>
    public static byte[] Create16kWav(float[] samples)
    {
        if (samples == null || samples.Length == 0)
        {
            return Array.Empty<byte>();
        }

        int sampleCount = samples.Length;
        int byteCount = sampleCount * 2; // 16-bit PCM = 2 bytes per sample
        byte[] wavBytes = new byte[44 + byteCount];

        using (var ms = new MemoryStream(wavBytes))
        using (var writer = new BinaryWriter(ms))
        {
            // 1. RIFF Chunk Descriptor
            writer.Write(new char[] { 'R', 'I', 'F', 'F' });
            writer.Write(36 + byteCount); // Overall file size minus 8 bytes
            writer.Write(new char[] { 'W', 'A', 'V', 'E' });

            // 2. fmt Sub-chunk
            writer.Write(new char[] { 'f', 'm', 't', ' ' });
            writer.Write(16); // Subchunk1Size (16 for PCM)
            writer.Write((short)1); // AudioFormat: 1 = PCM (linear quantization)
            writer.Write((short)TargetChannels); // NumChannels = 1 (Mono)
            writer.Write(TargetSampleRate); // SampleRate = 16000
            writer.Write(TargetSampleRate * TargetChannels * (TargetBitsPerSample / 8)); // ByteRate = 32000
            writer.Write((short)(TargetChannels * (TargetBitsPerSample / 8))); // BlockAlign = 2
            writer.Write((short)TargetBitsPerSample); // BitsPerSample = 16

            // 3. data Sub-chunk
            writer.Write(new char[] { 'd', 'a', 't', 'a' });
            writer.Write(byteCount);

            // Write 16-bit PCM samples
            for (int i = 0; i < sampleCount; i++)
            {
                float s = Math.Clamp(samples[i], -1.0f, 1.0f);
                short pcm = (short)Math.Round(s * 32767.0f);
                writer.Write(pcm);
            }
        }

        return wavBytes;
    }

    /// <summary>
    /// Calculates peak amplitude [0.0 - 1.0] of a float sample buffer.
    /// </summary>
    public static float CalculatePeak(float[] samples)
    {
        if (samples == null || samples.Length == 0) return 0f;
        float max = 0f;
        for (int i = 0; i < samples.Length; i++)
        {
            float abs = Math.Abs(samples[i]);
            if (abs > max) max = abs;
        }
        return Math.Clamp(max, 0f, 1f);
    }

    /// <summary>
    /// Calculates Root Mean Square (RMS) energy level [0.0 - 1.0] of a float sample buffer.
    /// </summary>
    public static float CalculateRms(float[] samples)
    {
        if (samples == null || samples.Length == 0) return 0f;
        double sumSquares = 0.0;
        for (int i = 0; i < samples.Length; i++)
        {
            sumSquares += samples[i] * samples[i];
        }
        return (float)Math.Clamp(Math.Sqrt(sumSquares / samples.Length), 0.0, 1.0);
    }
}
