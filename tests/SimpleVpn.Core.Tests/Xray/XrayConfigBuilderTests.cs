using System.Diagnostics;
using System.Text.Json.Nodes;
using SimpleVpn.Core.Models;
using SimpleVpn.Core.Parsing;
using SimpleVpn.Core.Routing;
using SimpleVpn.Core.Xray;

namespace SimpleVpn.Core.Tests.Xray;

public sealed class XrayConfigBuilderTests
{
    private static XrayConfigRequest Request(string link, RoutingPreset? preset = null, DnsSettings? dns = null) => new()
    {
        Profile = VlessLinkParser.Parse(link),
        SocksPort = 10888,
        HttpPort = 10889,
        Preset = preset ?? BuiltInPresets.Global,
        Dns = dns ?? new DnsSettings(),
    };

    private static JsonArray Rules(JsonObject config) => config["routing"]!["rules"]!.AsArray();

    private static RoutingPreset Custom(RouteOutbound defaultOutbound, params RoutingRule[] rules) => new()
    {
        Id = "user-test",
        Name = "Test",
        Rules = rules,
        DefaultOutbound = defaultOutbound,
    };

    public static TheoryData<string, string> LinksAndPresets()
    {
        var data = new TheoryData<string, string>();
        foreach (var link in TestLinks.AllLinks)
        {
            data.Add(link, BuiltInPresets.GlobalId);
        }

        foreach (var preset in BuiltInPresets.All.Where(p => p.Id != BuiltInPresets.GlobalId))
        {
            data.Add(TestLinks.XhttpTls, preset.Id);
        }

        return data;
    }

    private static JsonNode Stream(JsonObject config) => config["outbounds"]![0]!["streamSettings"]!;

    /// <summary>
    /// Golden file: the full config for the production setup. If it fails after an intended change,
    /// compare with the written <c>*.actual.json</c> and copy it over the golden file.
    /// </summary>
    /// <summary>
    /// Golden files: the full config for the production setup. If one fails after an intended change,
    /// compare with the written <c>*.actual.json</c> and copy it over the golden file.
    /// </summary>
    [Theory]
    [InlineData("xhttp-tls.json", BuiltInPresets.GlobalId)]
    [InlineData("xhttp-tls-bypass-ru.json", BuiltInPresets.BypassRuId)]
    public void Build_XhttpTls_MatchesGoldenFile(string fileName, string presetId)
    {
        var golden = Path.Combine(RepoPaths.Root, "tests", "SimpleVpn.Core.Tests", "Xray", "Golden", fileName);
        var request = Request(TestLinks.XhttpTls, BuiltInPresets.Get(presetId));
        var actual = XrayConfigBuilder.Build(request);

        var expected = File.Exists(golden) ? JsonNode.Parse(File.ReadAllText(golden)) : null;
        if (!JsonNode.DeepEquals(expected, actual))
        {
            File.WriteAllText(Path.ChangeExtension(golden, ".actual.json"), XrayConfigBuilder.BuildJson(request));
            Assert.Fail($"Config differs from {golden}; see the .actual.json next to it.");
        }
    }

    [Fact]
    public void Routing_RuleWithDomainsAndIps_IsSplitIntoAlternatives()
    {
        var preset = Custom(RouteOutbound.Proxy, new RoutingRule { Domain = ["domain:example.org"], Ip = ["203.0.113.0/24"], Port = "443", Outbound = RouteOutbound.Block });

        var rules = Rules(XrayConfigBuilder.Build(Request(TestLinks.XhttpTls, preset)));
        var blockRules = rules.Where(r => (string?)r!["outboundTag"] == "block").ToList();

        Assert.Equal(2, blockRules.Count);
        Assert.Equal("domain:example.org", (string?)blockRules[0]!["domain"]![0]);
        Assert.Null(blockRules[0]!["ip"]);
        Assert.Equal("203.0.113.0/24", (string?)blockRules[1]!["ip"]![0]);
        Assert.All(blockRules, r => Assert.Equal("443", (string?)r!["port"]));
    }

    [Fact]
    public void Routing_DisabledRulesAreSkipped_AndDefaultOutboundIsLast()
    {
        var preset = Custom(
            RouteOutbound.Direct,
            new RoutingRule { Domain = ["domain:a.example"], Outbound = RouteOutbound.Proxy },
            new RoutingRule { Domain = ["domain:b.example"], Outbound = RouteOutbound.Block, Enabled = false });

        var rules = Rules(XrayConfigBuilder.Build(Request(TestLinks.XhttpTls, preset)));

        Assert.DoesNotContain(rules, r => r!["domain"]?[0]?.GetValue<string>() == "domain:b.example");
        var last = rules[^1]!;
        Assert.Equal("tcp,udp", (string?)last["network"]);
        Assert.Equal("direct", (string?)last["outboundTag"]);
    }

    [Fact]
    public void Routing_DnsModuleQueriesGoThroughProxy_DirectResolverIpLeavesDirectly()
    {
        var config = XrayConfigBuilder.Build(Request(TestLinks.XhttpTls, dns: new DnsSettings { DirectServer = "77.88.8.8" }));
        var rules = Rules(config);

        Assert.Equal(XrayConfigBuilder.DnsTag, (string?)config["dns"]!["tag"]);
        Assert.Equal("77.88.8.8", (string?)rules[0]!["ip"]![0]);
        Assert.Equal("direct", (string?)rules[0]!["outboundTag"]);
        Assert.Equal(XrayConfigBuilder.DnsTag, (string?)rules[1]!["inboundTag"]![0]);
        Assert.Equal("proxy", (string?)rules[1]!["outboundTag"]);
    }

    [Fact]
    public void Dns_BypassRu_ResolvesRussianDomainsWithDirectResolver()
    {
        var dns = XrayConfigBuilder.Build(Request(TestLinks.XhttpTls, BuiltInPresets.Get(BuiltInPresets.BypassRuId)))["dns"]!;
        var servers = dns["servers"]!.AsArray();

        Assert.Equal(DnsSettings.DefaultRemoteServer, (string?)servers[0]);
        Assert.Equal("localhost", (string?)servers[1]!["address"]);
        var domains = servers[1]!["domains"]!.AsArray().Select(d => (string?)d).ToList();
        Assert.Contains("geosite:category-ru", domains);
        Assert.DoesNotContain("geosite:ru-blocked", domains);
        Assert.DoesNotContain("geosite:private", domains);
        Assert.Equal("UseIPv4", (string?)dns["queryStrategy"]);
    }

    [Fact]
    public void Dns_BlockedOnly_ResolvesBlockedDomainsWithRemoteResolver()
    {
        var servers = XrayConfigBuilder.Build(Request(TestLinks.XhttpTls, BuiltInPresets.Get(BuiltInPresets.BlockedOnlyId)))["dns"]!["servers"]!.AsArray();

        Assert.Equal("localhost", (string?)servers[0]);
        Assert.Equal(DnsSettings.DefaultRemoteServer, (string?)servers[1]!["address"]);
        Assert.Contains("geosite:ru-blocked", servers[1]!["domains"]!.AsArray().Select(d => (string?)d));
    }

    [Fact]
    public void Build_Reality_UsesRealitySettingsAndFlow()
    {
        var config = XrayConfigBuilder.Build(Request(TestLinks.RealityVision));

        var user = config["outbounds"]![0]!["settings"]!["vnext"]![0]!["users"]![0]!;
        Assert.Equal("xtls-rprx-vision", (string?)user["flow"]);
        var reality = Stream(config)["realitySettings"]!;
        Assert.Equal("www.microsoft.com", (string?)reality["serverName"]);
        Assert.Equal("Z84J2IelR9ch3k8VtlVhhs5ycBUlXA7wHBWcBrjqnAw", (string?)reality["publicKey"]);
        Assert.Equal("6ba85179e30d4fc2", (string?)reality["shortId"]);
        Assert.Null(Stream(config)["tlsSettings"]);
    }

    [Fact]
    public void Build_TlsWithoutSniOnIpAddress_OmitsServerName()
    {
        var config = XrayConfigBuilder.Build(Request($"vless://{TestLinks.Uuid}@203.0.113.5:443?security=tls"));

        Assert.Null(Stream(config)["tlsSettings"]!["serverName"]);
    }

    [Fact]
    public void Build_AllowInsecure_IsNotWrittenButPinningIs()
    {
        var tls = Stream(XrayConfigBuilder.Build(Request(TestLinks.PinnedCert)))["tlsSettings"]!;

        Assert.Null(tls["allowInsecure"]);
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", (string?)tls["pinnedPeerCertSha256"]);
        Assert.Equal("self.example.com", (string?)tls["verifyPeerCertByName"]);
    }

    [Fact]
    public void Build_InboundsListenOnLoopbackOnly()
    {
        var config = XrayConfigBuilder.Build(Request(TestLinks.XhttpTls));

        foreach (var inbound in config["inbounds"]!.AsArray())
        {
            Assert.Equal("127.0.0.1", (string?)inbound!["listen"]);
        }
    }

    /// <summary>
    /// Validates generated configs with the real pinned Xray binary and geo files (<c>xray run -test</c>):
    /// every transport with the global preset, and every built-in preset (this also checks that the
    /// geosite/geoip categories used by built-in presets exist in the shipped geo files).
    /// </summary>
    [Theory]
    [MemberData(nameof(LinksAndPresets))]
    public async Task Build_ProducesConfigAcceptedByXray(string link, string presetId)
    {
        Assert.SkipUnless(File.Exists(RepoPaths.XrayExe), "assets/core/xray.exe missing; run tools/fetch-xray.ps1");

        var dir = Directory.CreateTempSubdirectory("simplevpn-xray-test-");
        try
        {
            var configPath = Path.Combine(dir.FullName, "config.json");
            await File.WriteAllTextAsync(configPath, XrayConfigBuilder.BuildJson(Request(link, BuiltInPresets.Get(presetId))), TestContext.Current.CancellationToken);

            var psi = new ProcessStartInfo(RepoPaths.XrayExe, ["run", "-test", "-c", configPath])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.Environment["XRAY_LOCATION_ASSET"] = RepoPaths.GeoDirectory;

            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            var output = await stdout + await stderr;

            Assert.True(process.ExitCode == 0, $"xray -test failed ({process.ExitCode}):\n{output}");

            // Xray warns that the WS and gRPC transports themselves are deprecated (server-side choice);
            // any other deprecation means our config uses an outdated field.
            var deprecations = output.Split('\n')
                .Where(l => l.Contains("deprecated", StringComparison.OrdinalIgnoreCase))
                .Where(l => !l.Contains("WebSocket transport", StringComparison.Ordinal) && !l.Contains("gRPC transport", StringComparison.Ordinal));
            Assert.Empty(deprecations);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
