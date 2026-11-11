using Lidarr.Plugin.YtDlp.ArgParsing;
using NUnit.Framework;

namespace Lidarr.Plugin.YtDlp.Tests.ArgParsing;

[TestFixture]
public class ShellTokenizerTests
{
    [TestCase(null)]
    [TestCase("")]
    [TestCase("   \t ")]
    public void Empty_input_yields_no_tokens(string? input)
    {
        Assert.Multiple(() =>
        {
            Assert.That(ShellTokenizer.TryParse(input, out var tokens, out var error), Is.True, error);
            Assert.That(tokens, Is.Empty);
        });
    }

    [Test]
    public void Splits_on_whitespace()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ShellTokenizer.TryParse("--cookies-file  a.txt --retries 3", out var tokens, out _), Is.True);
            Assert.That(tokens, Is.EqualTo(new[] { "--cookies-file", "a.txt", "--retries", "3" }));
        });
    }

    [Test]
    public void Double_quoted_token_keeps_spaces()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ShellTokenizer.TryParse("--proxy \"https://user:pass@host:3128\" --retries 3", out var tokens, out _), Is.True);
            Assert.That(tokens, Is.EqualTo(new[] { "--proxy", "https://user:pass@host:3128", "--retries", "3" }));
        });
    }

    [Test]
    public void Single_quoted_token_keeps_spaces()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ShellTokenizer.TryParse("'-q --quiet' x", out var tokens, out _), Is.True);
            Assert.That(tokens, Is.EqualTo(new[] { "-q --quiet", "x" }));
        });
    }

    [Test]
    public void Unmatched_double_quote_fails()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ShellTokenizer.TryParse("--proxy \"https://x", out _, out var error), Is.False);
            Assert.That(error, Does.Contain("Unmatched"));
        });
    }

    [Test]
    public void Unmatched_single_quote_fails()
    {
        Assert.That(ShellTokenizer.TryParse("-o 'out.mp3", out _, out var error), Is.False);
        Assert.That(error, Does.Contain("Unmatched"));
    }

    [Test]
    public void Quoted_empty_string_fails()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ShellTokenizer.TryParse("--foo \"\"", out _, out var error), Is.False);
            Assert.That(error, Does.Contain("Empty argument"));
        });
    }

    [Test]
    public void Quoted_empty_string_at_end_fails()
    {
        Assert.That(ShellTokenizer.TryParse("x ''", out _, out var error), Is.False);
        Assert.That(error, Does.Contain("Empty argument"));
    }

    [Test]
    public void Mixed_quotes_and_bare_tokens()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ShellTokenizer.TryParse("a \"b c\" 'd e' f", out var tokens, out _), Is.True);
            Assert.That(tokens, Is.EqualTo(new[] { "a", "b c", "d e", "f" }));
        });
    }
}
