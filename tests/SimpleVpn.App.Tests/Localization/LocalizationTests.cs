using System.Globalization;
using System.Xml.Linq;
using SimpleVpn.App.Localization;

namespace SimpleVpn.App.Tests.Localization;

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
    public void Get_MissingKey_ReturnsMarkerInsteadOfThrowing()
    {
        Assert.Equal("[No_Such_Key]", Loc.Get("No_Such_Key"));
    }
}
