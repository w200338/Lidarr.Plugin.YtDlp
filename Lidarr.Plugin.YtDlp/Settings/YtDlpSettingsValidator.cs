using FluentValidation;
using Lidarr.Plugin.YtDlp.ArgParsing;
using NzbDrone.Core.Validation.Paths;

namespace Lidarr.Plugin.YtDlp.Settings;

/// <summary>
/// Shared (path-only) settings validation rules. Pure and deterministic (no
/// filesystem or network access; external connectivity is checked by the
/// connection tester).
///
/// The shared base POCO deliberately only carries the yt-dlp path (v0.2.4
/// split), so the shared rules only cover that field. Provider-specific rules
/// live on the two concrete validators, which mirror the fields each form
/// actually exposes.
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

/// <summary>Indexer form rules (path + search limit only).</summary>
public class YtDlpIndexerSettingsValidator : AbstractValidator<YtDlpIndexerSettings>
{
    public YtDlpIndexerSettingsValidator()
    {
        YtDlpSettingsValidator.ApplySharedRules(this);

        RuleFor(s => s.SearchLimit)
            .InclusiveBetween(1, 100).WithMessage("Search limit must be between 1 and 100.");
    }
}

/// <summary>Download client form rules (path + download-side fields only).</summary>
public class YtDlpClientSettingsValidator : AbstractValidator<YtDlpClientSettings>
{
    public YtDlpClientSettingsValidator()
    {
        YtDlpSettingsValidator.ApplySharedRules(this);

        RuleFor(s => s.AdditionalArgs)
            .MaximumLength(1024).WithMessage("Additional arguments are too long.")
            .Must(args => ShellTokenizer.TryParse(args, out _, out _))
            .WithMessage("Additional arguments could not be parsed. Quote values that contain spaces.");

        RuleFor(s => s.MinDownloadDelaySeconds)
            .InclusiveBetween(0d, 3600d).WithMessage("Minimum delay must be between 0 and 3600 seconds.");

        RuleFor(s => s.MaxDownloadDelaySeconds)
            .InclusiveBetween(0d, 86400d).WithMessage("Maximum delay must be between 0 and 86400 seconds.");

        // NB: FluentValidation's When() applies to the constraints declared before
        // it, so the predicate must come last to condition both rules.
        RuleFor(s => s.SponsorBlockApiEndpoint)
            .MaximumLength(512).WithMessage("SponsorBlock endpoint is too long.")
            .Must(YtDlpSettingsValidator.IsValidSponsorBlockEndpoint)
            .WithMessage("SponsorBlock endpoint must be a valid http(s) URL when SponsorBlock is enabled.")
            .When(s => s.UseSponsorBlock);

        RuleFor(s => s.DownloadPath)
            .NotEmpty().WithMessage("Download folder is required.")
            .MaximumLength(512).WithMessage("Download folder path is too long.")
            .IsValidPath().WithMessage("Download folder is not a valid path.");

        // Cross-field rule.
        RuleFor(s => s)
            .Must(s => s.MinDownloadDelaySeconds <= s.MaxDownloadDelaySeconds)
            .WithMessage("Minimum delay must be less than or equal to maximum delay.");
    }
}
