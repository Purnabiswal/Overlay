using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyOverlay;

public class AppSettings
{
    public string Text { get; set; } = "★ My Overlay Notes\r\n\r\n" +
                                        "• Drag top bar or empty areas to move\r\n" +
                                        "• Drag borders or corners to resize\r\n" +
                                        "• Press Ctrl+B to toggle Browser panel\r\n" +
                                        "• Press Ctrl+T for New Tab, Ctrl+W to Close Tab\r\n" +
                                        "• Press Ctrl+L to focus Address Bar\r\n" +
                                        "• Press Ctrl+Alt+S for Settings\r\n" +
                                        "• Press Ctrl+Alt+C for Click-Through mode\r\n" +
                                        "• Press Ctrl+Alt+H to Hide/Show window";

    public string FontFamily { get; set; } = "Segoe UI";
    public float FontSize { get; set; } = 12.0f;
    public bool FontBold { get; set; } = false;
    public bool FontItalic { get; set; } = false;

    public string TextColorHex { get; set; } = "#F8FAFC";
    public string BackgroundColorHex { get; set; } = "#18181B";
    public string BorderColorHex { get; set; } = "#6366F1";

    public double Opacity { get; set; } = 0.95;
    public int X { get; set; } = 100;
    public int Y { get; set; } = 100;
    public int Width { get; set; } = 800;
    public int Height { get; set; } = 550;

    public bool AlwaysOnTop { get; set; } = true;
    public bool ClickThrough { get; set; } = false;
    public bool ShowBorder { get; set; } = true;
    public int BorderThickness { get; set; } = 2;
    public bool ScreenCaptureProtection { get; set; } = true;

    // Browser settings
    public bool ShowBrowser { get; set; } = true;
    public string BrowserHomePage { get; set; } = "https://www.google.com";
    public string SearchEngineTemplate { get; set; } = "https://www.google.com/search?q={0}";
    // Stored relative to the active WebView viewport, so moving the overlay does not invalidate it.
    public int? ChatTargetX { get; set; }
    public int? ChatTargetY { get; set; }
    public string? ChatTargetUrl { get; set; }

    // Audio capture settings
    public bool CaptureSystemAudio { get; set; } = false;
    public string? SystemAudioDeviceId { get; set; } = null; // null = Windows Default Output
    public AudioSourceMode AudioSource { get; set; } = AudioSourceMode.SystemAudioOnly;
    public string? MicrophoneDeviceId { get; set; } = null; // null = Windows Default Microphone

    // Virtual audio routing settings (routes system audio to a virtual playback device so browser getUserMedia can hear it)
    public bool RouteToVirtualAudio { get; set; } = false;
    public string? VirtualAudioPlaybackDeviceId { get; set; } = "off"; // "off" = Output 1 disabled by default (input-only capture)
    public bool AutoGrantMicrophonePermission { get; set; } = true;
    public bool AutoSwitchDefaultRecordingDevice { get; set; } = false; // Kept false by default so Google Meet & other apps keep using real microphone instead of computer audio

    // VoiceMeeter-like Audio Routing Matrix
    public bool RouteSystemToVirtual { get; set; } = true;
    public bool RouteSystemToPhysical { get; set; } = false;
    public bool RouteMicToVirtual { get; set; } = true;
    public bool RouteMicToPhysical { get; set; } = false;
    public string? PhysicalOutputDeviceId { get; set; } = "off"; // "off" = Output 2 disabled by default (input-only capture)

    // Volume / Gain & Mute Controls (0.0 to 2.0 = 0% to 200%)
    public float SystemAudioVolume { get; set; } = 1.0f;
    public bool SystemAudioMuted { get; set; } = false;
    public float MicrophoneVolume { get; set; } = 1.0f;
    public bool MicrophoneMuted { get; set; } = false;
    public float VirtualOutputVolume { get; set; } = 1.0f;
    public bool VirtualOutputMuted { get; set; } = false;
    public float PhysicalOutputVolume { get; set; } = 1.0f;
    public bool PhysicalOutputMuted { get; set; } = false;

    // Continuous Speech-to-Text (Audio-to-Text) settings
    public string SpeechToTextEngine { get; set; } = "WindowsSpeech"; // "WindowsSpeech", "WhisperOpenAI", "WhisperGroq", "CustomEndpoint"
    public string SpeechToTextApiKey { get; set; } = "";
    public string SpeechToTextEndpoint { get; set; } = "https://api.openai.com/v1/audio/transcriptions";
    public string SpeechToTextModel { get; set; } = "whisper-1";
    public string SpeechToTextLanguage { get; set; } = "en";
    public float TranscriptionSilenceThreshold { get; set; } = 0.015f;
    public int TranscriptionMaxChunkSeconds { get; set; } = 5;

    [JsonIgnore]
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MyOverlay"
    );

    [JsonIgnore]
    private static readonly string SettingsFilePath = Path.Combine(SettingsDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                string json = File.ReadAllText(SettingsFilePath);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var settings = JsonSerializer.Deserialize<AppSettings>(json, options);
                if (settings != null)
                {
                    return settings;
                }
            }
        }
        catch
        {
            // If file is corrupted or unreadable, fall back to defaults
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            if (!Directory.Exists(SettingsDirectory))
            {
                Directory.CreateDirectory(SettingsDirectory);
            }

            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(this, options);
            File.WriteAllText(SettingsFilePath, json);
        }
        catch
        {
            // Ignore write failures (e.g. read-only environment)
        }
    }

    [JsonIgnore]
    public Font Font
    {
        get
        {
            FontStyle style = FontStyle.Regular;
            if (FontBold) style |= FontStyle.Bold;
            if (FontItalic) style |= FontStyle.Italic;

            try
            {
                return new Font(FontFamily, FontSize, style);
            }
            catch
            {
                return new Font("Segoe UI", FontSize, style);
            }
        }
    }

    public Color GetTextColor() => ParseColor(TextColorHex, Color.White);
    public Color GetBackgroundColor() => ParseColor(BackgroundColorHex, Color.FromArgb(24, 24, 27));
    public Color GetBorderColor() => ParseColor(BorderColorHex, Color.FromArgb(99, 102, 241));

    private static Color ParseColor(string hex, Color fallback)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(hex)) return fallback;
            return ColorTranslator.FromHtml(hex);
        }
        catch
        {
            return fallback;
        }
    }
}
