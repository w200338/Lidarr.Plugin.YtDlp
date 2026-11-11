using FluentValidation;
using Lidarr.Plugin.YtDlp.ArgParsing;

namespace Lidarr.Plugin.YtDlp.Settings;

/// <summary>
/// Pure/deterministic settings validation (no filesystem or network access; external
/// connectivity is checked by the connection tester, Phase 5).
/// </summary>
public sealed class YtDlpSettingsValidator : AbstractValidator<YtDlpSettings>
{
    public YtDlpSettingsValidator()
    {
        RuleFor(s => s.YtDlpPath)
            .NotEmpty().WithMessage("Path to yt-dlp is required.")
            .MaximumLength(512).WithMessage("yt-dlp path is too long.");

        RuleFor(s => s.AdditionalArgs)
            .MaximumLength(1024).WithMessage("Additional arguments are too long.")
            .Must(args => ShellTokenizer.TryParse(args, out _, out _))
            .WithMessage("Additional arguments could not be parsed. Quote values that contain spaces.");

        RuleFor(s => s.MinDownloadDelaySeconds)
            .InclusiveBetween(0, 3600).WithMessage("Minimum delay must be between 0 and 3600 seconds.");

        RuleFor(s => s.MaxDownloadDelaySeconds)
            .InclusiveBetween(0, 86400).WithMessage("Maximum delay must be between 0 and 86400 seconds.");

        RuleFor(s => s.SearchLimit)
            .InclusiveBetween(1, 100).WithMessage("Search limit must be between 1 and 100.");

        RuleFor(s => s.SponsorBlockApiEndpoint)
            .MaximumLength(512).WithMessage("SponsorBlock endpoint is too long.");

        // Cross-field rules.
        RuleFor(s => s)
            .Must(s => s.MinDownloadDelaySeconds <= s.MaxDownloadDelaySeconds)
            .WithMessage("Minimum delay must be less than or equal to maximum delay.");

        RuleFor(s => s)
            .Must(s => !s.UseSponsorBlock || IsValidSponsorBlockEndpoint(s.SponsorBlockApiEndpoint))
            .WithMessage("SponsorBlock endpoint must be a valid http(s) URL when SponsorBlock is enabled.");
    }

    /// <summary>
    /// True when the endpoint is (or normalizes to) a usable http(s) URL.
    /// Scheme-less values get https:// prepended (mirrors yt-dlp's behaviour);
    /// explicit non-http(s) schemes (file:, ftp:, ...) are rejected.
    /// </summary>
    internal static bool IsValidSponsorBlockEndpoint(string? raw)
    {
        var t = (raw ?? string.Empty).Trim();
        if (t.Length == 0)
        {
            return false;
        }

        if (!t.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !t.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (t.Contains("://"))
            {
                return false; // explicit non-http(s) scheme such as file:// or ftp://
            }

            t = "https://" + t;
        }

        return Uri.TryCreate(t, UriKind.Absolute, out _);
    }
}
