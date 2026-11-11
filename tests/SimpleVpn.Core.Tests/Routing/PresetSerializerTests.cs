using System.Globalization;
using SimpleVpn.Core.Routing;

namespace SimpleVpn.Core.Tests.Routing;

public sealed class PresetSerializerTests
{
    private const string Minimal = """
        {
          "name": "Mine",
          "rules": [
            { "domain": [" domain:example.org ", ""], "ip": ["192.168.1.1/24"], "port": "443, 8000-8080", "network": "TCP, UDP", "outbound": "block" }
          ],
          "defaultOutbound": "direct"
        }
        """;

    [Fact]
    public void BuiltIns_LoadInFixedOrderWithRules()
    {
        Assert.Equal([BuiltInPresets.GlobalId, BuiltInPresets.BypassRuId, BuiltInPresets.BlockedOnlyId], BuiltInPresets.All.Select(p => p.Id));
        Assert.All(BuiltInPresets.All, p => Assert.True(p.BuiltIn));
        Assert.All(BuiltInPresets.All, p => Assert.NotEmpty(p.Rules));
        Assert.Equal(RouteOutbound.Direct, BuiltInPresets.Get(BuiltInPresets.BlockedOnlyId).DefaultOutbound);
        Assert.Equal(BuiltInPresets.BypassRuId, BuiltInPresets.Default.Id);
    }

    [Theory]
    [InlineData("ru", "Всё, кроме RU")]
    [InlineData("en", "All except RU")]
    [InlineData("de", "All except RU")]
    public void DisplayName_UsesUiLanguageWithFallback(string culture, string expected)
    {
        Assert.Equal(expected, BuiltInPresets.Default.DisplayName(CultureInfo.GetCultureInfo(culture)));
    }

    [Fact]
    public void Parse_NormalizesValues()
    {
        var preset = PresetSerializer.Parse(Minimal);

        var rule = Assert.Single(preset.Rules);
        Assert.Equal(["domain:example.org"], rule.Domain);
        Assert.Equal("443,8000-8080", rule.Port);
        Assert.Equal("tcp,udp", rule.Network);
        Assert.Equal(RouteOutbound.Block, rule.Outbound);
        Assert.True(rule.Enabled);
        Assert.Equal(RouteOutbound.Direct, preset.DefaultOutbound);
        Assert.Equal("IPIfNonMatch", preset.DomainStrategy);
    }

    [Fact]
    public void ToJson_RoundTrips()
    {
        var preset = PresetSerializer.Parse(Minimal);

        var json = PresetSerializer.ToJson(preset);
        var again = PresetSerializer.Parse(json);

        Assert.Contains("\"outbound\": \"block\"", json, StringComparison.Ordinal);
        Assert.Equal(PresetSerializer.ToJson(again), json);
    }

    [Theory]
    [InlineData("not json", PresetError.Malformed)]
    [InlineData("""{ "name": "" }""", PresetError.MissingName)]
    [InlineData("""{ "name": "x", "schemaVersion": 99 }""", PresetError.UnsupportedVersion)]
    [InlineData("""{ "name": "x", "domainStrategy": "Fast" }""", PresetError.InvalidDomainStrategy)]
    [InlineData("""{ "name": "x", "rules": [ { "outbound": "proxy" } ] }""", PresetError.EmptyRule)]
    [InlineData("""{ "name": "x", "rules": [ { "domain": ["geosite:"] } ] }""", PresetError.InvalidDomain)]
    [InlineData("""{ "name": "x", "rules": [ { "domain": ["foo:bar"] } ] }""", PresetError.InvalidDomain)]
    [InlineData("""{ "name": "x", "rules": [ { "domain": ["regexp:(["] } ] }""", PresetError.InvalidDomain)]
    [InlineData("""{ "name": "x", "rules": [ { "ip": ["300.1.1.1"] } ] }""", PresetError.InvalidIp)]
    [InlineData("""{ "name": "x", "rules": [ { "ip": ["10.0.0.0/33"] } ] }""", PresetError.InvalidIp)]
    [InlineData("""{ "name": "x", "rules": [ { "ip": ["1"] } ] }""", PresetError.InvalidIp)]
    [InlineData("""{ "name": "x", "rules": [ { "port": "0" } ] }""", PresetError.InvalidPort)]
    [InlineData("""{ "name": "x", "rules": [ { "port": "90-80" } ] }""", PresetError.InvalidPort)]
    [InlineData("""{ "name": "x", "rules": [ { "network": "icmp" } ] }""", PresetError.InvalidNetwork)]
    [InlineData("""{ "name": "x", "rules": [ { "protocol": ["ftp"] } ] }""", PresetError.InvalidProtocol)]
    [InlineData("""{ "name": "x", "rules": [ { "domain": ["a.b"], "outbound": "nowhere" } ] }""", PresetError.Malformed)]
    public void Parse_Invalid_ReportsCode(string json, PresetError expected)
    {
        var ex = Assert.Throws<PresetException>(() => PresetSerializer.Parse(json));

        Assert.Equal(expected, ex.Error);
    }

    [Fact]
    public void Parse_InvalidRule_ReportsRuleNumber()
    {
        var ex = Assert.Throws<PresetException>(() => PresetSerializer.Parse("""{ "name": "x", "rules": [ { "domain": ["ok.example"] }, { "port": "abc" } ] }"""));

        Assert.Equal(2, ex.RuleNumber);
        Assert.Equal("abc", ex.Detail);
    }

    [Theory]
    [InlineData("geoip:ru")]
    [InlineData("!geoip:ru")]
    [InlineData("ext:custom.dat:list")]
    [InlineData("2001:db8::/32")]
    [InlineData("::1")]
    [InlineData("8.8.8.8")]
    public void ValidateRule_AcceptsIpForms(string ip)
    {
        PresetValidator.ValidateRule(new RoutingRule { Ip = [ip] });
    }

    [Fact]
    public void Parse_TooLarge_IsRejected()
    {
        var json = "{\"name\":\"" + new string('x', PresetSerializer.MaxSizeBytes) + "\"}";

        Assert.Equal(PresetError.TooLarge, Assert.Throws<PresetException>(() => PresetSerializer.Parse(json)).Error);
    }
}
