using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using SimpleVpn.Core.Models;

namespace SimpleVpn.Core.Xray;

public sealed record XrayConfigRequest
{
    public required ServerProfile Profile { get; init; }

    public required int SocksPort { get; init; }

    public required int HttpPort { get; init; }

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

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string BuildJson(XrayConfigRequest request) => Build(request).ToJsonString(Indented);

    public static JsonObject Build(XrayConfigRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new JsonObject
        {
            // Access log off: it would write the user's browsing history into our log files.
            ["log"] = new JsonObject { ["loglevel"] = request.LogLevel, ["access"] = "none" },
            ["inbounds"] = new JsonArray(
                Inbound("socks-in", "socks", request.SocksPort, new JsonObject { ["udp"] = true, ["auth"] = "noauth" }),
                Inbound("http-in", "http", request.HttpPort, new JsonObject())),
            ["outbounds"] = new JsonArray(
                ProxyOutbound(request.Profile),
                new JsonObject { ["tag"] = DirectTag, ["protocol"] = "freedom" },
                new JsonObject { ["tag"] = BlockTag, ["protocol"] = "blackhole" }),
            // TODO(M2): routing presets and DNS. For now: everything via proxy except private networks.
            ["routing"] = new JsonObject
            {
                ["domainStrategy"] = "AsIs",
                ["rules"] = new JsonArray(
                    new JsonObject
                    {
                        ["type"] = "field",
                        ["ip"] = new JsonArray("geoip:private"),
                        ["outboundTag"] = DirectTag,
                    }),
            },
        };
    }

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
