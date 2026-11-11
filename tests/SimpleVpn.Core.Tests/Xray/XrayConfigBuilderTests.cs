using System.Diagnostics;
using System.Text.Json.Nodes;
using SimpleVpn.Core.Parsing;
using SimpleVpn.Core.Xray;

namespace SimpleVpn.Core.Tests.Xray;

public sealed class XrayConfigBuilderTests
{
    private static XrayConfigRequest Request(string link) => new()
    {
        Profile = VlessLinkParser.Parse(link),
        SocksPort = 10888,
        HttpPort = 10889,
    };

    private static JsonNode Stream(JsonObject config) => config["outbounds"]![0]!["streamSettings"]!;

    /// <summary>
    /// Golden file: the full config for the production setup. If it fails after an intended change,
    /// compare with the written <c>*.actual.json</c> and copy it over the golden file.
    /// </summary>
    [Fact]
    public void Build_XhttpTls_MatchesGoldenFile()
    {
        var golden = Path.Combine(RepoPaths.Root, "tests", "SimpleVpn.Core.Tests", "Xray", "Golden", "xhttp-tls.json");
        var actual = XrayConfigBuilder.Build(Request(TestLinks.XhttpTls));

        var expected = JsonNode.Parse(File.ReadAllText(golden));
        if (!JsonNode.DeepEquals(expected, actual))
        {
            File.WriteAllText(Path.ChangeExtension(golden, ".actual.json"), XrayConfigBuilder.BuildJson(Request(TestLinks.XhttpTls)));
            Assert.Fail($"Config differs from {golden}; see the .actual.json next to it.");
        }
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

    /// <summary>Validates generated configs with the real pinned Xray binary (<c>xray run -test</c>).</summary>
    [Theory]
    [MemberData(nameof(TestLinks.All), MemberType = typeof(TestLinks))]
    public async Task Build_ProducesConfigAcceptedByXray(string link)
    {
        Assert.SkipUnless(File.Exists(RepoPaths.XrayExe), "assets/core/xray.exe missing; run tools/fetch-xray.ps1");

        var dir = Directory.CreateTempSubdirectory("simplevpn-xray-test-");
        try
        {
            var configPath = Path.Combine(dir.FullName, "config.json");
            await File.WriteAllTextAsync(configPath, XrayConfigBuilder.BuildJson(Request(link)), TestContext.Current.CancellationToken);

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
