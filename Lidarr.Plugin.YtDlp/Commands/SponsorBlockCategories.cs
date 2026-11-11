namespace Lidarr.Plugin.YtDlp.Commands;

/// <summary>SponsorBlock category set that trims non-music parts from songs.</summary>
public static class SponsorBlockCategories
{
    /// <summary>
    /// Categories to remove for "keep only the music":
    /// music_offtopic (intro/outro chatter around the actual song), sponsor, intro, outro,
    /// selfpromo, preview, interaction (sub reminders), hook (attention-grabbing opening).
    /// 'filler' is deliberately excluded — on music tracks it is too aggressive.
    /// </summary>
    public static readonly string[] NonMusic =
    [
        "music_offtopic", "sponsor", "intro", "outro", "selfpromo", "preview", "interaction", "hook"
    ];

    public static string NonMusicJoined => string.Join(",", NonMusic);
}
