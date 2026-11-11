using System.Collections.Concurrent;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SimpleVpn.Core.Geo;

namespace SimpleVpn.Desktop;

/// <summary>Flag pictures (Assets/flags/&lt;code&gt;.png from tools/fetch-flags.ps1) by country code, cached.</summary>
public static class ServerFlags
{
    private static readonly ConcurrentDictionary<string, Bitmap?> Cache = new(StringComparer.Ordinal);

    /// <summary>Country code (<c>ServerProfile.Country</c>) to a flag picture; null for unknown codes.</summary>
    public static IValueConverter Converter { get; } = new FuncValueConverter<string?, Bitmap?>(For);

    public static Bitmap? For(string? code) =>
        CountryFlags.IsCountryCode(code) ? Cache.GetOrAdd(code!.ToLowerInvariant(), Load) : null;

    private static Bitmap? Load(string code)
    {
        var uri = new Uri($"avares://SimpleVpn/Assets/flags/{code}.png");
        return AssetLoader.Exists(uri) ? new Bitmap(AssetLoader.Open(uri)) : null;
    }
}
