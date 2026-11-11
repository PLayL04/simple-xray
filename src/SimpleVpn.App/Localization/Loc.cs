using System.Globalization;
using System.Resources;

namespace SimpleVpn.App.Localization;

/// <summary>
/// Access to localized UI strings. Russian is the neutral language (Strings.resx),
/// English lives in Strings.en.resx. The culture is chosen once at startup;
/// switching the language requires restarting the app.
/// </summary>
public static class Loc
{
    private static readonly ResourceManager Resources =
        new("SimpleVpn.App.Localization.Strings", typeof(Loc).Assembly);

    public static IReadOnlyList<CultureInfo> SupportedCultures { get; } =
        [CultureInfo.GetCultureInfo("ru"), CultureInfo.GetCultureInfo("en")];

    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("ru");

    /// <summary>Sets the UI culture. Unsupported cultures fall back to Russian.</summary>
    public static void SetCulture(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        var language = culture.TwoLetterISOLanguageName;
        Culture = SupportedCultures.FirstOrDefault(c => c.TwoLetterISOLanguageName == language) ?? SupportedCultures[0];
        CultureInfo.CurrentUICulture = Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
    }

    /// <summary>Returns the localized string, or <c>[key]</c> if it is missing (visible in UI, never throws).</summary>
    public static string Get(string key) => Resources.GetString(key, Culture) ?? $"[{key}]";
}
