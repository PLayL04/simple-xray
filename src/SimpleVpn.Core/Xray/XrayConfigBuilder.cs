using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using SimpleVpn.Core.Models;
using SimpleVpn.Core.Routing;

namespace SimpleVpn.Core.Xray;

public sealed record XrayConfigRequest
{
    public required ServerProfile Profile { get; init; }

    public required int SocksPort { get; init; }

    public required int HttpPort { get; init; }

    /// <summary>Routing preset; defaults to "everything via proxy".</summary>
    public RoutingPreset Preset { get; init; } = BuiltInPresets.Global;

    public DnsSettings Dns { get; init; } = new();

    public ConnectionMode Mode { get; init; } = ConnectionMode.SystemProxy;

    public TunSettings Tun { get; init; } = new();

    /// <summary>Xray log level: debug, info, warning, error, none.</summary>
    public string LogLevel { get; init; } = "warning";
}

/// <summary>
/// The only place where Xray JSON configuration is produced.
/// Docs: https://xtls.github.io/config/
/// </summary>
public static class XrayConfigBuilder
{
    public const string ProxyTag = "proxy";
    public const string DirectTag = "direct";
    public const string BlockTag = "block";
    public const string Loopback = "127.0.0.1";

    /// <summary>Inbound tag of queries made by Xray's own DNS module (used to route them).</summary>
    public const string DnsTag = "dns-module";

    public const string TunTag = "tun-in";
    public const string DnsOutTag = "dns-out";

    /// <summary>Wintun adapter name. Fixed, so the adapter is reused and can be found by name.</summary>
    public const string TunName = "SimpleVpn";

    /// <summary>Addresses of the TUN adapter (private /30 and ULA /126, unlikely to clash with home networks).</summary>
    public static readonly IReadOnlyList<string> TunGateway = ["172.19.0.1/30", "fdfe:dcba:9876::1/126"];

    /// <summary>DNS server announced on the TUN; its queries come back to Xray and go to the DNS module.</summary>
    public const string TunDnsServer = "172.19.0.2";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string BuildJson(XrayConfigRequest request) => Build(request).ToJsonString(Indented);

    public static JsonObject Build(XrayConfigRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var tun = request.Mode == ConnectionMode.Tun;
        var inbounds = new JsonArray();
        if (tun)
        {
            // First, so the adapter is set up before the SOCKS port (our readiness signal) starts listening.
            inbounds.Add((JsonNode)TunInbound(request.Tun));
        }

        // SOCKS/HTTP stay in TUN mode too: the readiness check and downloads through the proxy use them.
        inbounds.Add((JsonNode)Inbound("socks-in", "socks", request.SocksPort, new JsonObject { ["udp"] = true, ["auth"] = "noauth" }));
        inbounds.Add((JsonNode)Inbound("http-in", "http", request.HttpPort, new JsonObject()));

        var outbounds = new JsonArray(
            ProxyOutbound(request.Profile),
            new JsonObject { ["tag"] = DirectTag, ["protocol"] = "freedom" },
            new JsonObject { ["tag"] = BlockTag, ["protocol"] = "blackhole" });
        if (tun)
        {
            outbounds.Add((JsonNode)new JsonObject { ["tag"] = DnsOutTag, ["protocol"] = "dns" });
        }

        return new JsonObject
        {
            // Access log off: it would write the user's browsing history into our log files.
            ["log"] = new JsonObject { ["loglevel"] = request.LogLevel, ["access"] = "none" },
            ["inbounds"] = inbounds,
            ["outbounds"] = outbounds,
            ["dns"] = Dns(request.Preset, request.Dns),
            ["routing"] = Routing(request.Preset, request.Dns, tun),
        };
    }

    /// <summary>
    /// Xray TUN inbound (keys checked against the v26.11.9 sources, infra/conf/tun.go and proxy/tun/tun_windows.go):
    /// Xray creates the Wintun adapter, routes all IPv4/IPv6 to it, binds its own outbound sockets to the physical
    /// interface (autoOutboundsInterface "auto", which prevents loops), and optionally blocks DNS leaks with WFP.
    /// </summary>
    private static JsonObject TunInbound(TunSettings settings)
    {
        var tunSettings = new JsonObject
        {
            ["name"] = TunName,
            ["desc"] = TunName,
            ["mtu"] = 1500,
            ["gateway"] = StringArray(TunGateway),
            ["dns"] = new JsonArray(TunDnsServer),
            ["autoSystemRoutingTable"] = new JsonArray("0.0.0.0/0", "::/0"),
            ["autoOutboundsInterface"] = "auto",
        };
        if (settings.BlockDnsLeaks)
        {
            tunSettings["autoSystemWfpBlockLeak"] = new JsonArray("dns");
        }

        return new JsonObject
        {
            ["tag"] = TunTag,
            ["protocol"] = "tun",
            ["settings"] = tunSettings,
            ["sniffing"] = new JsonObject
            {
                ["enabled"] = true,
                ["destOverride"] = new JsonArray("http", "tls", "quic"),
                ["routeOnly"] = true,
            },
        };
    }

    /// <summary>
    /// Domains that leave directly are resolved by the direct resolver, the rest by the remote one through the proxy.
    /// For presets where unmatched traffic goes direct it is the other way round: proxied domains get the remote resolver.
    /// </summary>
    private static JsonObject Dns(RoutingPreset preset, DnsSettings dns)
    {
        var defaultIsDirect = preset.DefaultOutbound == RouteOutbound.Direct;
        var scopedOutbound = defaultIsDirect ? RouteOutbound.Proxy : RouteOutbound.Direct;
        var scopedDomains = preset.Rules
            .Where(r => r.Enabled && r.Outbound == scopedOutbound && IsUnconditional(r))
            .SelectMany(r => r.Domain)
            .Where(IsDnsDomainMatcher)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var (fallback, scoped) = defaultIsDirect ? (dns.DirectServer, dns.RemoteServer) : (dns.RemoteServer, dns.DirectServer);
        var servers = new JsonArray((JsonNode)fallback);
        if (scopedDomains.Length > 0)
        {
            servers.Add((JsonNode)new JsonObject
            {
                ["address"] = scoped,
                ["domains"] = new JsonArray([.. scopedDomains.Select(d => (JsonNode)d)]),
                ["skipFallback"] = true,
            });
        }

        return new JsonObject
        {
            ["servers"] = servers,
            ["queryStrategy"] = dns.PreferIPv4 ? "UseIPv4" : "UseIP",
            ["tag"] = DnsTag,
        };
    }

    private static JsonObject Routing(RoutingPreset preset, DnsSettings dns, bool tun)
    {
        var rules = new JsonArray();

        // TUN: the system's DNS queries (to the server announced on the adapter, or any port 53) go to
        // Xray's DNS module, which resolves them per preset (direct resolver / remote one through the proxy).
        if (tun)
        {
            rules.Add((JsonNode)new JsonObject
            {
                ["inboundTag"] = new JsonArray(TunTag),
                ["port"] = "53",
                ["outboundTag"] = DnsOutTag,
            });
        }

        // Xray's own DNS queries: the direct resolver leaves directly, everything else goes through the proxy.
        // ("localhost" is the system resolver and does not pass through routing at all.)
        if (IPAddress.TryParse(dns.DirectServer, out _))
        {
            rules.Add((JsonNode)new JsonObject
            {
                ["inboundTag"] = new JsonArray(DnsTag),
                ["ip"] = new JsonArray(dns.DirectServer),
                ["outboundTag"] = DirectTag,
            });
        }

        rules.Add((JsonNode)new JsonObject { ["inboundTag"] = new JsonArray(DnsTag), ["outboundTag"] = ProxyTag });

        foreach (var rule in preset.Rules.Where(r => r.Enabled))
        {
            // Domains and IPs in one preset rule are alternatives; in one Xray rule they would both have to match.
            if (rule.Domain.Count > 0)
            {
                rules.Add((JsonNode)XrayRule(rule, domain: rule.Domain, ip: null));
            }

            if (rule.Ip.Count > 0)
            {
                rules.Add((JsonNode)XrayRule(rule, domain: null, ip: rule.Ip));
            }

            if (rule.Domain.Count == 0 && rule.Ip.Count == 0)
            {
                rules.Add((JsonNode)XrayRule(rule, domain: null, ip: null));
            }
        }

        // Unmatched traffic. Without this rule Xray would use the first outbound (proxy).
        rules.Add((JsonNode)new JsonObject { ["network"] = "tcp,udp", ["outboundTag"] = OutboundTag(preset.DefaultOutbound) });

        return new JsonObject
        {
            ["domainStrategy"] = preset.DomainStrategy,
            ["rules"] = rules,
        };
    }

    private static JsonObject XrayRule(RoutingRule rule, IReadOnlyList<string>? domain, IReadOnlyList<string>? ip)
    {
        var result = new JsonObject();
        if (domain is not null)
        {
            result["domain"] = StringArray(domain);
        }

        if (ip is not null)
        {
            result["ip"] = StringArray(ip);
        }

        AddIfSet(result, "port", rule.Port);
        AddIfSet(result, "network", rule.Network);
        if (rule.Protocol.Count > 0)
        {
            result["protocol"] = StringArray(rule.Protocol);
        }

        if (rule.Process.Count > 0)
        {
            result["process"] = StringArray(rule.Process);
        }

        result["outboundTag"] = OutboundTag(rule.Outbound);
        return result;
    }

    private static string OutboundTag(RouteOutbound outbound) => outbound switch
    {
        RouteOutbound.Direct => DirectTag,
        RouteOutbound.Block => BlockTag,
        _ => ProxyTag,
    };

    /// <summary>A rule whose domains always decide the outbound (no extra port/network/... conditions).</summary>
    private static bool IsUnconditional(RoutingRule rule) =>
        string.IsNullOrEmpty(rule.Port) && string.IsNullOrEmpty(rule.Network) && rule.Protocol.Count == 0 && rule.Process.Count == 0;

    /// <summary>Matchers understood by the DNS <c>domains</c> list (same syntax as routing).</summary>
    private static bool IsDnsDomainMatcher(string domain) => !domain.StartsWith("geosite:private", StringComparison.Ordinal);

    private static JsonArray StringArray(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode)v)]);

    private static JsonObject Inbound(string tag, string protocol, int port, JsonObject settings) => new()
    {
        ["tag"] = tag,
        ["listen"] = Loopback,
        ["port"] = port,
        ["protocol"] = protocol,
        ["settings"] = settings,
        ["sniffing"] = new JsonObject
        {
            ["enabled"] = true,
            ["destOverride"] = new JsonArray("http", "tls", "quic"),
            ["routeOnly"] = true,
        },
    };

    private static JsonObject ProxyOutbound(ServerProfile p)
    {
        if (!string.Equals(p.Protocol, "vless", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"Protocol '{p.Protocol}' is not supported.");
        }

        var user = new JsonObject { ["id"] = p.UserId, ["encryption"] = p.Encryption };
        if (!string.IsNullOrEmpty(p.Flow))
        {
            user["flow"] = p.Flow;
        }

        return new JsonObject
        {
            ["tag"] = ProxyTag,
            ["protocol"] = "vless",
            ["settings"] = new JsonObject
            {
                ["vnext"] = new JsonArray(new JsonObject
                {
                    ["address"] = p.Address,
                    ["port"] = p.Port,
                    ["users"] = new JsonArray(user),
                }),
            },
            ["streamSettings"] = StreamSettings(p),
        };
    }

    private static JsonObject StreamSettings(ServerProfile p)
    {
        var network = p.Network.ToLowerInvariant();
        var stream = new JsonObject { ["network"] = network, ["security"] = p.Security };

        switch (network)
        {
            case "xhttp":
                var xhttp = new JsonObject();
                AddIfSet(xhttp, "host", p.Host);
                AddIfSet(xhttp, "path", p.Path);
                AddIfSet(xhttp, "mode", p.XhttpMode);
                if (!string.IsNullOrEmpty(p.XhttpExtra))
                {
                    xhttp["extra"] = JsonNode.Parse(p.XhttpExtra);
                }

                stream["xhttpSettings"] = xhttp;
                break;

            case "ws":
                var ws = new JsonObject();
                AddIfSet(ws, "path", p.Path);
                AddIfSet(ws, "host", p.Host);
                stream["wsSettings"] = ws;
                break;

            case "httpupgrade":
                var upgrade = new JsonObject();
                AddIfSet(upgrade, "path", p.Path);
                AddIfSet(upgrade, "host", p.Host);
                stream["httpupgradeSettings"] = upgrade;
                break;

            case "grpc":
                var grpc = new JsonObject();
                AddIfSet(grpc, "serviceName", p.GrpcServiceName);
                stream["grpcSettings"] = grpc;
                break;
        }

        switch (p.Security)
        {
            case "tls":
                var tls = new JsonObject();
                AddIfSet(tls, "serverName", ServerName(p));
                AddIfSet(tls, "fingerprint", p.Fingerprint);
                if (p.Alpn.Count > 0)
                {
                    tls["alpn"] = new JsonArray([.. p.Alpn.Select(a => (JsonNode)a)]);
                }

                // "allowInsecure" was removed from Xray; certificate checks can only be relaxed by pinning.
                AddIfSet(tls, "pinnedPeerCertSha256", p.PinnedPeerCertSha256);
                AddIfSet(tls, "verifyPeerCertByName", p.VerifyPeerCertByName);

                stream["tlsSettings"] = tls;
                break;

            case "reality":
                var reality = new JsonObject();
                AddIfSet(reality, "serverName", ServerName(p));
                reality["fingerprint"] = string.IsNullOrEmpty(p.Fingerprint) ? "chrome" : p.Fingerprint;
                AddIfSet(reality, "publicKey", p.RealityPublicKey);
                AddIfSet(reality, "shortId", p.RealityShortId);
                AddIfSet(reality, "spiderX", p.RealitySpiderX);
                AddIfSet(reality, "mldsa65Verify", p.RealityMldsa65Verify);
                stream["realitySettings"] = reality;
                break;
        }

        return stream;
    }

    /// <summary>SNI: explicit sni, else Host header, else the address when it is a domain name.</summary>
    private static string? ServerName(ServerProfile p)
    {
        if (!string.IsNullOrEmpty(p.Sni))
        {
            return p.Sni;
        }

        if (!string.IsNullOrEmpty(p.Host))
        {
            return p.Host;
        }

        return IPAddress.TryParse(p.Address, out _) ? null : p.Address;
    }

    private static void AddIfSet(JsonObject obj, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            obj[key] = value;
        }
    }
}
