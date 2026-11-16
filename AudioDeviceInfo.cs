using System;
using System.Collections.Generic;
using NAudio.CoreAudioApi;

namespace MyOverlay;

public enum AudioDeviceClassification
{
    Disabled,
    PhysicalMicrophone,
    StereoMix,
    VirtualDevice,
    SpeakersOrHeadphones,
    Other
}

public class AudioDeviceInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public bool IsPlayback { get; set; }
    public DeviceState State { get; set; } = DeviceState.Active;
    public AudioDeviceClassification Classification { get; set; } = AudioDeviceClassification.Disabled;

    public string ClassificationDisplay
    {
        get
        {
            if (string.Equals(Id, "off", StringComparison.OrdinalIgnoreCase)) return "Disabled";
            return Classification switch
            {
                AudioDeviceClassification.Disabled => "Disabled",
                AudioDeviceClassification.PhysicalMicrophone => "Physical Microphone",
                AudioDeviceClassification.StereoMix => "Windows Stereo Mix (Loopback)",
                AudioDeviceClassification.VirtualDevice => "Virtual Audio Device / Cable",
                AudioDeviceClassification.SpeakersOrHeadphones => "Physical Audio Output",
                _ => "Audio Device"
            };
        }
    }

    public string StateDisplay
    {
        get
        {
            return State switch
            {
                DeviceState.Active => "Active",
                DeviceState.Disabled => "Disabled",
                DeviceState.NotPresent => "Not Present",
                DeviceState.Unplugged => "Unplugged",
                _ => State.ToString()
            };
        }
    }

    public static AudioDeviceClassification Classify(string name, bool isPlayback)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            string.Equals(name, "off", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "none", StringComparison.OrdinalIgnoreCase))
        {
            return AudioDeviceClassification.Disabled;
        }

        if (!isPlayback)
        {
            if (name.Contains("Stereo Mix", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("What U Hear", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Wave Out Mix", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Stereomix", StringComparison.OrdinalIgnoreCase))
            {
                return AudioDeviceClassification.StereoMix;
            }

            if (name.Contains("Steam", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("CABLE", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("VoiceMeeter", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("VAC", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("OBS", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Line (", StringComparison.OrdinalIgnoreCase))
            {
                return AudioDeviceClassification.VirtualDevice;
            }

            return AudioDeviceClassification.PhysicalMicrophone;
        }
        else
        {
            if (name.Contains("Steam", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("CABLE", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("VoiceMeeter", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("OBS", StringComparison.OrdinalIgnoreCase))
            {
                return AudioDeviceClassification.VirtualDevice;
            }

            return AudioDeviceClassification.SpeakersOrHeadphones;
        }
    }

    public override string ToString()
    {
        if (string.Equals(Id, "off", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Name, "Off", StringComparison.OrdinalIgnoreCase))
        {
            return "Off";
        }

        if (string.Equals(Id, "none", StringComparison.OrdinalIgnoreCase))
        {
            return Name;
        }

        string badge = Classification switch
        {
            AudioDeviceClassification.Disabled => "Disabled",
            AudioDeviceClassification.PhysicalMicrophone => "🎤 Physical Mic",
            AudioDeviceClassification.StereoMix => "📻 Stereo Mix",
            AudioDeviceClassification.VirtualDevice => "🎛️ Virtual Audio",
            AudioDeviceClassification.SpeakersOrHeadphones => "🔊 Output",
            _ => IsPlayback ? "🔊 Output" : "🎤 Mic"
        };

        string defTag = IsDefault ? " [DEFAULT]" : "";
        string stateTag = State != DeviceState.Active ? $" ({StateDisplay})" : "";
        return $"{Name} [{badge}]{defTag}{stateTag}";
    }
}
