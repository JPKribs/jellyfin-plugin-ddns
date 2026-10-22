using System.Text;
using System.Xml;

// Declared in the plugin's root namespace on purpose: every plugin type lives below it, so a call to
// FormatUtilities anywhere in the plugin finds this class before the shared library's one that a
// using directive brings in, without any call site having to change.
namespace Jellyfin.Plugin.DynamicDns;

/// <summary>
/// Formatting helpers for log lines and stored status text. Wraps the shared library's helpers so text
/// that ends up in the activity log or the status store is also safe to save as XML.
/// </summary>
public static class FormatUtilities
{
    /// <summary>
    /// Formats a byte count as a human-readable string.
    /// </summary>
    /// <param name="bytes">The byte count.</param>
    /// <returns>The formatted size.</returns>
    public static string FormatBytes(long bytes) => JPKribs.Jellyfin.Base.FormatUtilities.FormatBytes(bytes);

    /// <summary>
    /// Makes outside text safe to log and store: control characters and characters XML cannot hold are
    /// removed, including a lone surrogate, and the result is cut to at most <paramref name="maxLength"/>
    /// characters without splitting a surrogate pair. Empty or null renders as "[empty]".
    /// </summary>
    /// <param name="input">The text, such as a provider reply.</param>
    /// <param name="maxLength">The most UTF-16 characters kept before "..." is added.</param>
    /// <returns>The cleaned text.</returns>
    public static string SanitizeForLog(string? input, int maxLength = 200)
    {
        if (string.IsNullOrEmpty(input))
        {
            return "[empty]";
        }

        var limit = maxLength < 0 ? 0 : maxLength;
        var output = new StringBuilder(input.Length < limit ? input.Length : limit);
        var truncated = false;
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (char.IsHighSurrogate(c) && i + 1 < input.Length && char.IsLowSurrogate(input[i + 1]))
            {
                // A whole pair, which XML always accepts. It is kept or dropped as one.
                if (output.Length + 2 > limit)
                {
                    truncated = true;
                    break;
                }

                output.Append(c).Append(input[i + 1]);
                i++;
                continue;
            }

            if (char.IsSurrogate(c) || char.IsControl(c) || !XmlConvert.IsXmlChar(c))
            {
                continue;
            }

            if (output.Length + 1 > limit)
            {
                truncated = true;
                break;
            }

            output.Append(c);
        }

        return truncated ? output.Append("...").ToString() : output.ToString();
    }
}
