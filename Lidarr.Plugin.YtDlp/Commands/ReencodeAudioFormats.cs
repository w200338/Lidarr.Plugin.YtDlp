using Lidarr.Plugin.YtDlp.Settings;

namespace Lidarr.Plugin.YtDlp.Commands;

/// <summary>Maps plugin re-encode settings to yt-dlp <c>--audio-format</c> values.</summary>
public static class ReencodeAudioFormats
{
    /// <summary>
    /// yt-dlp format name for the setting. AAC maps to 'm4a' (AAC in an MP4 container)
    /// which is what users expect from an "AAC" option.
    /// </summary>
    public static string? GetYtDlpFormat(ReencodeFormat format) => format switch
    {
        ReencodeFormat.Disabled => null,
        ReencodeFormat.Mp3 => "mp3",
        ReencodeFormat.Aac => "m4a",
        ReencodeFormat.Opus => "opus",
        ReencodeFormat.Vorbis => "vorbis",
        _ => null
    };
}
