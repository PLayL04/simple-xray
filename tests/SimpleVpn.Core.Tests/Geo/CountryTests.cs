using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleVpn.Core.Geo;
using SimpleVpn.Core.Parsing;
using SimpleVpn.Core.Storage;

namespace SimpleVpn.Core.Tests.Geo;

public sealed class CountryTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("svpn-country-");

    public void Dispose() => _root.Delete(recursive: true);

    [Theory]
    [InlineData("🇳🇱 Amsterdam", "nl")]
    [InlineData("Frankfurt 🇩🇪", "de")]
    [InlineData("🇺🇸🇩🇪 Mixed", "us")]
    [InlineData("Plain name", null)]
    [InlineData("", null)]
    public void CodeFromName(string name, string? expected) => Assert.Equal(expected, CountryFlags.CodeFromName(name));

    [Theory]
    [InlineData("🇳🇱 Amsterdam", "Amsterdam")]
    [InlineData("Home 🇩🇪  server", "Home server")]
    [InlineData("🇳🇱", "🇳🇱")]
    [InlineData("Plain  name", "Plain  name")]
    public void StripFlags(string name, string expected) => Assert.Equal(expected, CountryFlags.StripFlags(name));

    /// <summary>Hand-built geoip.dat: a category list, a reversed entry, then real countries.</summary>
    private string WriteGeoip()
    {
        static byte[] Varint(ulong v)
        {
            var list = new List<byte>();
            do
            {
                var b = (byte)(v & 0x7F);
                v >>= 7;
                list.Add(v != 0 ? (byte)(b | 0x80) : b);
            }
            while (v != 0);
            return [.. list];
        }

        static byte[] Field(int number, byte[] payload) => [.. Varint((ulong)((number << 3) | 2)), .. Varint((ulong)payload.Length), .. payload];
        static byte[] Cidr(string ip, int prefix) =>
            Field(2, [.. Field(1, IPAddress.Parse(ip).GetAddressBytes()), .. Varint(0x10), .. Varint((ulong)prefix)]);
        static byte[] Entry(string code, bool reverse, params byte[][] cidrs)
        {
            byte[] body = [.. Field(1, System.Text.Encoding.UTF8.GetBytes(code)), .. cidrs.SelectMany(c => c)];
            if (reverse)
            {
                body = [.. body, .. Varint(0x18), 1];
            }

            return Field(1, body);
        }

        var path = Path.Combine(_root.FullName, "geoip.dat");
        File.WriteAllBytes(path,
        [
            .. Entry("ru-blocked", false, Cidr("203.0.113.0", 24)),
            .. Entry("xx", true, Cidr("203.0.113.0", 24)),
            .. Entry("NL", false, Cidr("198.51.100.0", 25), Cidr("203.0.113.128", 25)),
            .. Entry("de", false, Cidr("203.0.113.0", 25), Cidr("2001:db8::", 32)),
        ]);
        return path;
    }

    [Theory]
    [InlineData("203.0.113.5", "de")]
    [InlineData("203.0.113.200", "nl")]
    [InlineData("198.51.100.1", "nl")]
    [InlineData("198.51.100.200", null)]
    [InlineData("2001:db8::1", "de")]
    [InlineData("::ffff:203.0.113.5", "de")]
    public void GeoIp_FindsCountryAndSkipsCategoriesAndReversed(string ip, string? expected) =>
        Assert.Equal(expected, GeoIpCountry.Find(WriteGeoip(), IPAddress.Parse(ip)));

    [Fact]
    public void GeoIp_RealFile_KnownAddresses()
    {
        var real = Path.Combine(RepoPaths.Root, "assets", "geo", "geoip.dat");
        Assert.SkipUnless(File.Exists(real), "assets/geo/geoip.dat is not downloaded (tools/fetch-xray.ps1).");

        Assert.Equal("ru", GeoIpCountry.Find(real, IPAddress.Parse("77.88.44.242")));
        Assert.Equal("de", GeoIpCountry.Find(real, IPAddress.Parse("85.214.132.117")));
    }

    [Fact]
    public async Task Service_FillsCountryOnlyForServersWithoutFlag()
    {
        var paths = AppPaths.Resolve(_root.FullName, _root.FullName, _root.FullName, _ => false);
        Directory.CreateDirectory(paths.GeoDirectory);
        File.Move(WriteGeoip(), Path.Combine(paths.GeoDirectory, "geoip.dat"));
        var profiles = new ProfileRepository(paths);
        var plain = profiles.AddOrUpdate(VlessLinkParser.Parse(TestLinks.XhttpTls) with { Address = "203.0.113.200", Name = "Plain" });
        var flagged = profiles.AddOrUpdate(VlessLinkParser.Parse(TestLinks.XhttpTls) with { Address = "203.0.113.5", Name = "🇫🇷 Paris" });
        using var service = new ServerCountryService(profiles, paths, NullLogger<ServerCountryService>.Instance);

        await service.UpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal("nl", profiles.Profiles.Single(p => p.Id == plain.Id).CountryCode);
        Assert.Null(profiles.Profiles.Single(p => p.Id == flagged.Id).CountryCode);
        Assert.Equal(flagged.Id, profiles.Selected?.Id);
        Assert.Equal("nl", new ProfileRepository(paths).Profiles.Single(p => p.Id == plain.Id).CountryCode);
    }

    [Fact]
    public async Task Service_FreshDataFolder_UsesShippedGeoip()
    {
        var paths = AppPaths.Resolve(_root.FullName, _root.FullName, _root.FullName, _ => false);
        Directory.CreateDirectory(paths.SeedGeoDirectory);
        File.Move(WriteGeoip(), Path.Combine(paths.SeedGeoDirectory, "geoip.dat"));
        File.WriteAllBytes(Path.Combine(paths.SeedGeoDirectory, "geosite.dat"), [0x0A, 0x03, 0x0A, 0x01, 0x78]);
        var profiles = new ProfileRepository(paths);
        var server = profiles.AddOrUpdate(VlessLinkParser.Parse(TestLinks.XhttpTls) with { Address = "203.0.113.5", Name = "Plain" });
        using var service = new ServerCountryService(profiles, paths, NullLogger<ServerCountryService>.Instance);

        await service.UpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal("de", profiles.Profiles.Single(p => p.Id == server.Id).CountryCode);
    }
}
