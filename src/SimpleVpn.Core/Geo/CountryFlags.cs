using System.Text;

namespace SimpleVpn.Core.Geo;

/// <summary>
/// Flag emoji are pairs of "regional indicator" symbols (U+1F1E6..U+1F1FF = A..Z). 3x-ui puts one into
/// the server name (e.g. "🇳🇱 Amsterdam"). Windows fonts cannot draw them (they show as letters),
/// so the UI shows a flag picture instead and strips the emoji from the name.
/// </summary>
public static class CountryFlags
{
    private const int FirstIndicator = 0x1F1E6;
    private const int LastIndicator = 0x1F1FF;

    /// <summary>Lower-case ISO 3166 code of the first flag emoji in the text, or null.</summary>
    public static string? CodeFromName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        char? first = null;
        foreach (var rune in name.EnumerateRunes())
        {
            if (IsIndicator(rune))
            {
                var letter = (char)('a' + (rune.Value - FirstIndicator));
                if (first is null)
                {
                    first = letter;
                    continue;
                }

                return string.Concat(first.Value, letter);
            }

            first = null;
        }

        return null;
    }

    /// <summary>The name without flag emoji (and the spaces they leave), for display next to a flag picture.</summary>
    public static string StripFlags(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (CodeFromName(name) is null)
        {
            return name;
        }

        var builder = new StringBuilder(name.Length);
        foreach (var rune in name.EnumerateRunes())
        {
            if (!IsIndicator(rune))
            {
                builder.Append(rune.ToString());
            }
        }

        var stripped = string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return stripped.Length > 0 ? stripped : name;
    }

    /// <summary>Two ASCII letters: a country code as used in geoip.dat ("nl"), not a category like "ru-blocked".</summary>
    public static bool IsCountryCode(string? code) =>
        code is { Length: 2 } && char.IsAsciiLetter(code[0]) && char.IsAsciiLetter(code[1]);

    private static bool IsIndicator(Rune rune) => rune.Value is >= FirstIndicator and <= LastIndicator;
}
