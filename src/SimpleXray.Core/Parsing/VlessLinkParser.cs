using System.Globalization;
using System.Text;
using System.Text.Json;
using SimpleXray.Core.Models;

namespace SimpleXray.Core.Parsing;

/// <summary>
/// Parses and builds <c>vless://uuid@host:port?params#name</c> share links
/// (the format produced by 3x-ui / v2rayN / v2rayNG).
/// </summary>
public static class VlessLinkParser
{
    public const string Scheme = "vless://";

    private static readonly HashSet<string> KnownKeys = new(StringComparer.Ordinal)
    {
        "encryption", "flow", "type", "security", "sni", "fp", "alpn", "allowInsecure", "insecure", "pcs", "vcn",
        "pbk", "sid", "spx", "pqv", "path", "host", "mode", "extra", "serviceName", "headerType",
    };

    private static readonly HashSet<string> SupportedSecurity = new(StringComparer.Ordinal) { "none", "tls", "reality" };

    public static bool LooksLikeLink(string? text) =>
        text is not null && text.TrimStart().StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    /// <exception cref="LinkParseException">The link is not a valid vless:// link.</exception>
    public static ServerProfile Parse(string link)
    {
        if (string.IsNullOrWhiteSpace(link))
        {
            throw new LinkParseException(LinkParseError.Empty, "Link is empty.");
        }

        link = link.Trim();
        if (!link.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            throw new LinkParseException(LinkParseError.UnsupportedScheme, "Only vless:// links are supported.");
        }

        var rest = link[Scheme.Length..];

        var name = string.Empty;
        var hashIndex = rest.IndexOf('#', StringComparison.Ordinal);
        if (hashIndex >= 0)
        {
            name = Unescape(rest[(hashIndex + 1)..]).Trim();
            rest = rest[..hashIndex];
        }

        var query = string.Empty;
        var queryIndex = rest.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex >= 0)
        {
            query = rest[(queryIndex + 1)..];
            rest = rest[..queryIndex];
        }

        var authority = rest.TrimEnd('/');
        var atIndex = authority.LastIndexOf('@');
        if (atIndex < 0)
        {
            throw new LinkParseException(LinkParseError.MissingUserId, "User id is missing.");
        }

        var userId = Unescape(authority[..atIndex]);
        ValidateUserId(userId);
        var (address, port) = ParseHostPort(authority[(atIndex + 1)..]);

        var parameters = ParseQuery(query);
        string? Take(string key) => parameters.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

        var security = (Take("security") ?? "none").ToLowerInvariant();
        if (!SupportedSecurity.Contains(security))
        {
            throw new LinkParseException(LinkParseError.InvalidParameter, $"Unsupported security '{security}'.");
        }

        var network = (Take("type") ?? "tcp").ToLowerInvariant();
        if (network == "splithttp")
        {
            network = "xhttp";
        }

        var extra = Take("extra");
        if (extra is not null)
        {
            try
            {
                using var _ = JsonDocument.Parse(extra);
            }
            catch (JsonException ex)
            {
                throw new LinkParseException(LinkParseError.InvalidParameter, $"Parameter 'extra' is not valid JSON: {ex.Message}");
            }
        }

        var insecure = Take("allowInsecure") ?? Take("insecure");

        return new ServerProfile
        {
            Name = name,
            Address = address,
            Port = port,
            UserId = userId,
            Encryption = Take("encryption") ?? "none",
            Flow = Take("flow"),
            Network = network,
            Security = security,
            Sni = Take("sni"),
            Fingerprint = Take("fp"),
            Alpn = Take("alpn")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [],
            AllowInsecure = insecure is "1" or "true" or "True",
            PinnedPeerCertSha256 = Take("pcs"),
            VerifyPeerCertByName = Take("vcn"),
            RealityPublicKey = Take("pbk"),
            RealityShortId = Take("sid"),
            RealitySpiderX = Take("spx"),
            RealityMldsa65Verify = Take("pqv"),
            Path = Take("path"),
            Host = Take("host"),
            XhttpMode = Take("mode"),
            XhttpExtra = extra,
            GrpcServiceName = Take("serviceName"),
            HeaderType = Take("headerType"),
            ExtraParameters = parameters
                .Where(kv => !KnownKeys.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
        };
    }

    /// <summary>Builds a share link equivalent to the one the profile was imported from.</summary>
    public static string ToLink(ServerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var query = new List<KeyValuePair<string, string>>();
        void Add(string key, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                query.Add(new(key, value));
            }
        }

        Add("encryption", profile.Encryption);
        Add("flow", profile.Flow);
        Add("type", profile.Network);
        Add("security", profile.Security);
        Add("sni", profile.Sni);
        Add("fp", profile.Fingerprint);
        Add("alpn", profile.Alpn.Count > 0 ? string.Join(',', profile.Alpn) : null);
        Add("allowInsecure", profile.AllowInsecure ? "1" : null);
        Add("pcs", profile.PinnedPeerCertSha256);
        Add("vcn", profile.VerifyPeerCertByName);
        Add("pbk", profile.RealityPublicKey);
        Add("sid", profile.RealityShortId);
        Add("spx", profile.RealitySpiderX);
        Add("pqv", profile.RealityMldsa65Verify);
        Add("path", profile.Path);
        Add("host", profile.Host);
        Add("mode", profile.XhttpMode);
        Add("extra", profile.XhttpExtra);
        Add("serviceName", profile.GrpcServiceName);
        Add("headerType", profile.HeaderType);
        foreach (var kv in profile.ExtraParameters.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            Add(kv.Key, kv.Value);
        }

        var host = profile.Address.Contains(':', StringComparison.Ordinal) ? $"[{profile.Address}]" : profile.Address;
        var sb = new StringBuilder(Scheme)
            .Append(Uri.EscapeDataString(profile.UserId))
            .Append('@').Append(host)
            .Append(':').Append(profile.Port.ToString(CultureInfo.InvariantCulture));

        if (query.Count > 0)
        {
            sb.Append('?').AppendJoin('&', query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        }

        if (!string.IsNullOrEmpty(profile.Name))
        {
            sb.Append('#').Append(Uri.EscapeDataString(profile.Name));
        }

        return sb.ToString();
    }

    private static void ValidateUserId(string userId)
    {
        if (userId.Length == 0)
        {
            throw new LinkParseException(LinkParseError.MissingUserId, "User id is missing.");
        }

        // Xray accepts a UUID or any string of 1-30 bytes (mapped to UUIDv5).
        if (!Guid.TryParse(userId, out _) &&
            (Encoding.UTF8.GetByteCount(userId) > 30 || userId.Any(char.IsWhiteSpace)))
        {
            throw new LinkParseException(LinkParseError.InvalidUserId, "User id is not a valid UUID.");
        }
    }

    private static (string Address, int Port) ParseHostPort(string hostPort)
    {
        string address;
        string portText;

        if (hostPort.StartsWith('['))
        {
            var close = hostPort.IndexOf(']', StringComparison.Ordinal);
            if (close < 0 || close + 1 >= hostPort.Length || hostPort[close + 1] != ':')
            {
                throw new LinkParseException(LinkParseError.Malformed, "Invalid IPv6 address or missing port.");
            }

            address = hostPort[1..close];
            portText = hostPort[(close + 2)..];
        }
        else
        {
            var colon = hostPort.LastIndexOf(':');
            if (colon < 0)
            {
                throw new LinkParseException(LinkParseError.InvalidPort, "Port is missing.");
            }

            address = hostPort[..colon];
            portText = hostPort[(colon + 1)..];
        }

        if (string.IsNullOrWhiteSpace(address))
        {
            throw new LinkParseException(LinkParseError.MissingAddress, "Server address is missing.");
        }

        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            throw new LinkParseException(LinkParseError.InvalidPort, $"Invalid port '{portText}'.");
        }

        return (address, port);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            var key = Unescape(eq < 0 ? pair : pair[..eq]);
            var value = eq < 0 ? string.Empty : Unescape(pair[(eq + 1)..]);
            if (key.Length > 0)
            {
                result[key] = value;
            }
        }

        return result;
    }

    private static string Unescape(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException ex)
        {
            throw new LinkParseException(LinkParseError.Malformed, $"Invalid escape sequence: {ex.Message}");
        }
    }
}
