using System.Text;

namespace Lidarr.Plugin.YtDlp.ArgParsing;

/// <summary>
/// Minimal shell-like tokenizer for user-supplied CLI argument strings.
/// Whitespace separates tokens; single or double quotes make a token literal
/// (no escape sequences). An unmatched quote or an empty quoted token is an error.
/// </summary>
public static class ShellTokenizer
{
    public static bool TryParse(string? input, out IReadOnlyList<string> tokens, out string error)
    {
        tokens = Array.Empty<string>();
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
        {
            return true; // no args at all is fine
        }

        var result = new List<string>();
        var current = new StringBuilder();
        var inToken = false;
        char? quote = null;

        foreach (var c in input)
        {
            if (quote != null)
            {
                if (c == quote)
                {
                    quote = null;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '\'' || c == '"')
            {
                quote = c;
                inToken = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (inToken)
                {
                    if (current.Length == 0)
                    {
                        error = "Empty argument token (a quoted empty string?) is not allowed.";
                        return false;
                    }
                    result.Add(current.ToString());
                    current.Clear();
                    inToken = false;
                }
            }
            else
            {
                current.Append(c);
                inToken = true;
            }
        }

        if (quote != null)
        {
            error = $"Unmatched '{quote}' in additional arguments.";
            return false;
        }

        if (inToken)
        {
            if (current.Length == 0)
            {
                error = "Empty argument token (a quoted empty string?) is not allowed.";
                return false;
            }
            result.Add(current.ToString());
        }

        tokens = result;
        return true;
    }
}
