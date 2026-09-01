using System.ComponentModel;
using ModelContextProtocol.Server;
using NAudio.CoreAudioApi;

namespace Zero.SystemControl.Tools;

[McpServerToolType]
public sealed class AudioTools
{
    private static MMDeviceEnumerator GetEnumerator() => new();

    private static MMDevice GetDefaultDevice() =>
        GetEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

    // ─── set_volume ───────────────────────────────────────────────────────────

    [McpServerTool, Description("Set the system master volume level (0–100).")]
    public static string set_volume(
        [Description("Volume level from 0 (mute) to 100 (max).")] int level)
    {
        if (level < 0 || level > 100)
            return "Error: level must be between 0 and 100.";

        using var device = GetDefaultDevice();
        device.AudioEndpointVolume.MasterVolumeLevelScalar = level / 100f;
        device.AudioEndpointVolume.Mute = false;
        return $"OK: volume set to {level}%";
    }

    // ─── get_volume ───────────────────────────────────────────────────────────

    [McpServerTool, Description("Get the current system master volume level.")]
    public static string get_volume()
    {
        using var device = GetDefaultDevice();
        var level = (int)Math.Round(device.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
        var muted = device.AudioEndpointVolume.Mute;
        return $"Volume: {level}%{(muted ? " (muted)" : "")}";
    }

    // ─── mute ─────────────────────────────────────────────────────────────────

    [McpServerTool, Description("Mute the system audio.")]
    public static string mute()
    {
        using var device = GetDefaultDevice();
        device.AudioEndpointVolume.Mute = true;
        return "OK: audio muted";
    }

    // ─── unmute ───────────────────────────────────────────────────────────────

    [McpServerTool, Description("Unmute the system audio.")]
    public static string unmute()
    {
        using var device = GetDefaultDevice();
        device.AudioEndpointVolume.Mute = false;
        return "OK: audio unmuted";
    }
}
