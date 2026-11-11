using System.Text.Json;
using SimpleVpn.Core.Parsing;

namespace SimpleVpn.Core.Tests.Parsing;

public sealed class VlessLinkParserTests
{
    [Fact]
    public void Parse_XhttpTls_ReadsAllParameters()
    {
        var p = VlessLinkParser.Parse(TestLinks.XhttpTls);

        Assert.Equal("Нидерланды", p.Name);
        Assert.Equal("vpn.example.com", p.Address);
        Assert.Equal(443, p.Port);
        Assert.Equal(TestLinks.Uuid, p.UserId);
        Assert.Equal("none", p.Encryption);
        Assert.Equal("xhttp", p.Network);
        Assert.Equal("tls", p.Security);
        Assert.Equal("/xh", p.Path);
        Assert.Equal("vpn.example.com", p.Host);
        Assert.Equal("auto", p.XhttpMode);
        Assert.Equal("vpn.example.com", p.Sni);
        Assert.Equal("chrome", p.Fingerprint);
        Assert.Equal(["h2", "http/1.1"], p.Alpn);
        Assert.Equal("""{"xPaddingBytes":"100-1000"}""", p.XhttpExtra);
        Assert.Empty(p.ExtraParameters);
    }

    [Fact]
    public void Parse_Reality_ReadsRealityParameters()
    {
        var p = VlessLinkParser.Parse(TestLinks.RealityVision);

        Assert.Equal("reality", p.Security);
        Assert.Equal("tcp", p.Network);
        Assert.Equal("xtls-rprx-vision", p.Flow);
        Assert.Equal("Z84J2IelR9ch3k8VtlVhhs5ycBUlXA7wHBWcBrjqnAw", p.RealityPublicKey);
        Assert.Equal("6ba85179e30d4fc2", p.RealityShortId);
        Assert.Equal("/", p.RealitySpiderX);
        Assert.Equal("www.microsoft.com", p.Sni);
    }

    [Fact]
    public void Parse_Ipv6Host_StripsBrackets()
    {
        var p = VlessLinkParser.Parse(TestLinks.WsTlsIpv6);

        Assert.Equal("2001:db8::1", p.Address);
        Assert.Equal(8443, p.Port);
        Assert.Equal("/ws?ed=2048", p.Path);
    }

    [Fact]
    public void Parse_SplitHttpAlias_IsNormalizedToXhttp()
    {
        var p = VlessLinkParser.Parse($"vless://{TestLinks.Uuid}@a.example.com:443?type=splithttp&security=tls");

        Assert.Equal("xhttp", p.Network);
    }

    [Fact]
    public void Parse_UnknownParameters_ArePreserved()
    {
        var p = VlessLinkParser.Parse($"vless://{TestLinks.Uuid}@a.example.com:443?security=tls&ech=abc&futureKey=1");

        Assert.Equal("abc", p.ExtraParameters["ech"]);
        Assert.Equal("1", p.ExtraParameters["futureKey"]);
    }

    [Fact]
    public void Parse_TrimsWhitespaceAndAcceptsUppercaseScheme()
    {
        var p = VlessLinkParser.Parse($"  VLESS://{TestLinks.Uuid}@a.example.com:443  \n");

        Assert.Equal("a.example.com", p.Address);
        Assert.Equal("tcp", p.Network);
        Assert.Equal("none", p.Security);
    }

    [Theory]
    [InlineData("", LinkParseError.Empty)]
    [InlineData("   ", LinkParseError.Empty)]
    [InlineData("vmess://abc", LinkParseError.UnsupportedScheme)]
    [InlineData("https://example.com", LinkParseError.UnsupportedScheme)]
    [InlineData("vless://a.example.com:443", LinkParseError.MissingUserId)]
    [InlineData("vless://@a.example.com:443", LinkParseError.MissingUserId)]
    [InlineData("vless://not a uuid at all, really long string@a.example.com:443", LinkParseError.InvalidUserId)]
    [InlineData("vless://0b5c2a8e-6f1d-4c3a-9e2b-7d4f8a1c3e5b@:443", LinkParseError.MissingAddress)]
    [InlineData("vless://0b5c2a8e-6f1d-4c3a-9e2b-7d4f8a1c3e5b@a.example.com", LinkParseError.InvalidPort)]
    [InlineData("vless://0b5c2a8e-6f1d-4c3a-9e2b-7d4f8a1c3e5b@a.example.com:0", LinkParseError.InvalidPort)]
    [InlineData("vless://0b5c2a8e-6f1d-4c3a-9e2b-7d4f8a1c3e5b@a.example.com:70000", LinkParseError.InvalidPort)]
    [InlineData("vless://0b5c2a8e-6f1d-4c3a-9e2b-7d4f8a1c3e5b@a.example.com:abc", LinkParseError.InvalidPort)]
    [InlineData("vless://0b5c2a8e-6f1d-4c3a-9e2b-7d4f8a1c3e5b@[2001:db8::1:443", LinkParseError.Malformed)]
    [InlineData("vless://0b5c2a8e-6f1d-4c3a-9e2b-7d4f8a1c3e5b@a.example.com:443?security=xtls", LinkParseError.InvalidParameter)]
    [InlineData("vless://0b5c2a8e-6f1d-4c3a-9e2b-7d4f8a1c3e5b@a.example.com:443?type=xhttp&extra=%7Bbad", LinkParseError.InvalidParameter)]
    public void Parse_InvalidLink_ThrowsWithErrorCode(string link, LinkParseError expected)
    {
        var ex = Assert.Throws<LinkParseException>(() => VlessLinkParser.Parse(link));

        Assert.Equal(expected, ex.Error);
    }

    [Theory]
    [MemberData(nameof(TestLinks.All), MemberType = typeof(TestLinks))]
    public void ToLink_RoundTrip_PreservesProfile(string link)
    {
        var original = VlessLinkParser.Parse(link);

        var reparsed = VlessLinkParser.Parse(VlessLinkParser.ToLink(original)) with { Id = original.Id };

        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(reparsed));
    }

    [Theory]
    [InlineData("vless://x")]
    [InlineData(" vless://")]
    public void LooksLikeLink_DetectsScheme(string text) => Assert.True(VlessLinkParser.LooksLikeLink(text));

    [Theory]
    [InlineData(null)]
    [InlineData("hello")]
    public void LooksLikeLink_RejectsOtherText(string? text) => Assert.False(VlessLinkParser.LooksLikeLink(text));
}
