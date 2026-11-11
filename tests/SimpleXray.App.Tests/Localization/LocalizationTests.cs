using System.Globalization;
using System.Xml.Linq;
using SimpleXray.App.Localization;

namespace SimpleXray.App.Tests.Localization;

public sealed class LocalizationTests
{
    private static Dictionary<string, string> ReadResx(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Localization", fileName);
        return XDocument.Load(path).Root!
            .Elements("data")
            .ToDictionary(e => (string)e.Attribute("name")!, e => (string?)e.Element("value") ?? string.Empty);
    }

    [Fact]
    public void AllLanguages_HaveSameKeys()
    {
        var ru = ReadResx("Strings.resx");
        var en = ReadResx("Strings.en.resx");

        Assert.Empty(ru.Keys.Except(en.Keys));
        Assert.Empty(en.Keys.Except(ru.Keys));
    }

    [Theory]
    [InlineData("Strings.resx")]
    [InlineData("Strings.en.resx")]
    public void AllStrings_AreNotEmpty(string fileName)
    {
        var empty = ReadResx(fileName).Where(kv => string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key);

        Assert.Empty(empty);
    }

    [Fact]
    public void Get_ReturnsStringForSelectedCulture()
    {
        try
        {
            Loc.SetCulture(CultureInfo.GetCultureInfo("en-US"));
            Assert.Equal("Connect", Loc.Get("Action_Connect"));

            Loc.SetCulture(CultureInfo.GetCultureInfo("ru-RU"));
            Assert.Equal("Подключить", Loc.Get("Action_Connect"));
        }
        finally
        {
            Loc.SetCulture(CultureInfo.GetCultureInfo("ru"));
        }
    }

    [Fact]
    public void SetCulture_UnsupportedLanguage_FallsBackToRussian()
    {
        Loc.SetCulture(CultureInfo.GetCultureInfo("de-DE"));

        Assert.Equal("ru", Loc.Culture.TwoLetterISOLanguageName);
    }

    [Fact]
    public void EveryErrorCode_HasLocalizedMessage()
    {
        var keys = ReadResx("Strings.resx").Keys.ToHashSet();
        var expected = Enum.GetNames<Core.Connection.ConnectionErrorCode>().Select(n => $"Error_{n}")
            .Concat(Enum.GetNames<Core.Parsing.LinkParseError>().Select(n => $"LinkError_{n}"))
            .Concat(Enum.GetNames<Core.Routing.PresetError>().Select(n => $"PresetError_{n}"))
            .Concat(Enum.GetNames<Core.Geo.GeoUpdateError>().Select(n => $"GeoError_{n}"))
            .Concat(Core.Models.GeoSettings.IntervalChoicesHours.Select(h => $"Interval_{h}"));

        Assert.All(expected, key => Assert.Contains(key, keys));
    }

    [Fact]
    public void Get_MissingKey_ReturnsMarkerInsteadOfThrowing()
    {
        Assert.Equal("[No_Such_Key]", Loc.Get("No_Such_Key"));
    }
}
