namespace Lidarr.Plugin.YtDlp.Settings;

/// <summary>
/// Re-encode target for downloaded audio. <c>Disabled</c> keeps the best available
/// source audio container as-is; all other values force extraction + encode via ffmpeg
/// at the highest quality.
/// </summary>
public enum ReencodeFormat
{
    Disabled = 0,
    Mp3 = 1,
    Aac = 2,
    Opus = 3,
    Vorbis = 4
}
