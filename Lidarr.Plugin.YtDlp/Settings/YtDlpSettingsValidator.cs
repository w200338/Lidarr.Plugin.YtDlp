using FluentValidation;
using Lidarr.Plugin.YtDlp.ArgParsing;

namespace Lidarr.Plugin.YtDlp.Settings;

/// <summary>
/// Pure/deterministic settings validation (no filesystem or network access; external
/// connectivity is checked by the connection tester).
///
/// The rules live in <see cref="ApplySharedRules{T}"/> so the provider settings
/// validators (indexer/client) can apply the same shared rules to their own
/// concrete settings types and add provider-specific rules on top.
/// </summary>
public class YtDlpSettingsValidator : AbstractValidator<YtDlpSettings>
{
    public YtDlpSettingsValidator()
    {
        ApplySharedRules(this);
    }

    public static void ApplySharedRules<T>(AbstractValidator<T> validator) where T : YtDlpSettings
    {
        validator.RuleFor(s => s.YtDlpPath)
            .NotEmpty().WithMessage("Path to yt-dlp is required.")
            .MaximumLength(512).WithMessage("yt-dlp path is too long.");

        validator.RuleFor(s => s.AdditionalArgs)
            .MaximumLength(1024).WithMessage("Additional arguments are too long.")
            .Must(args => ShellTokenizer.TryParse(args, out _, out _))
            .WithMessage("Additional arguments could not be parsed. Quote values that contain spaces.");

        validator.RuleFor(s => s.MinDownloadDelaySeconds)
            .InclusiveBetween(0, 3600).WithMessage("Minimum delay must be between 0 and 3600 seconds.");

        validator.RuleFor(s => s.MaxDownloadDelaySeconds)
            .InclusiveBetween(0, 86400).WithMessage("Maximum delay must be between 0 and 86400 seconds.");

        validator.RuleFor(s => s.SearchLimit)
            .InclusiveBetween(1, 100).WithMessage("Search limit must be between 1 and 100.");

        // NB: FluentValidation's When() applies to the constraints declared before
        // it, so the predicate must come last to condition both rules.
        validator.RuleFor(s => s.SponsorBlockApiEndpoint)
            .MaximumLength(512).WithMessage("SponsorBlock endpoint is too long.")
            .Must(IsValidSponsorBlockEndpoint)
            .WithMessage("SponsorBlock endpoint must be a valid http(s) URL when SponsorBlock is enabled.")
            .When(s => s.UseSponsorBlock);

        // Cross-field rule.
        validator.RuleFor(s => s)
            .Must(s => s.MinDownloadDelaySeconds <= s.MaxDownloadDelaySeconds)
            .WithMessage("Minimum delay must be less than or equal to maximum delay.");
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
