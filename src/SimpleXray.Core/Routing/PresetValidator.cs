using System.Net;
using System.Text.RegularExpressions;

namespace SimpleXray.Core.Routing;

/// <summary>
/// Checks a preset before it is saved or used, so mistakes surface in the editor with a clear message
/// instead of as an Xray start failure. Category names (geosite/geoip) are checked by Xray itself.
/// </summary>
public static class PresetValidator
{
    public static readonly IReadOnlyList<string> DomainStrategies = ["AsIs", "IPIfNonMatch", "IPOnDemand"];

    public static readonly IReadOnlyList<string> Networks = ["tcp", "udp", "tcp,udp"];

    public static readonly IReadOnlyList<string> Protocols = ["http", "tls", "quic", "bittorrent"];

    private static readonly string[] DomainPrefixes = ["geosite:", "domain:", "full:", "keyword:", "regexp:", "ext:", "dotless:"];

    public static void Validate(RoutingPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);

        if (preset.SchemaVersion > RoutingPreset.CurrentSchemaVersion)
        {
            throw new PresetException(PresetError.UnsupportedVersion, preset.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (string.IsNullOrWhiteSpace(preset.Name) && (preset.Names is null || preset.Names.Values.All(string.IsNullOrWhiteSpace)))
        {
            throw new PresetException(PresetError.MissingName);
        }

        if (!DomainStrategies.Contains(preset.DomainStrategy, StringComparer.Ordinal))
        {
            throw new PresetException(PresetError.InvalidDomainStrategy, preset.DomainStrategy);
        }

        for (var i = 0; i < preset.Rules.Count; i++)
        {
            try
            {
                ValidateRule(preset.Rules[i]);
            }
            catch (PresetException ex)
            {
                throw new PresetException(ex.Error, ex.Detail) { RuleNumber = i + 1 };
            }
        }
    }

    public static void ValidateRule(RoutingRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (!rule.HasMatcher)
        {
            throw new PresetException(PresetError.EmptyRule);
        }

        foreach (var domain in rule.Domain)
        {
            ValidateDomain(domain);
        }

        foreach (var ip in rule.Ip)
        {
            ValidateIp(ip);
        }

        if (!string.IsNullOrEmpty(rule.Port) && !IsValidPortList(rule.Port))
        {
            throw new PresetException(PresetError.InvalidPort, rule.Port);
        }

        if (!string.IsNullOrEmpty(rule.Network) && !Networks.Contains(rule.Network, StringComparer.Ordinal))
        {
            throw new PresetException(PresetError.InvalidNetwork, rule.Network);
        }

        foreach (var protocol in rule.Protocol)
        {
            if (!Protocols.Contains(protocol, StringComparer.Ordinal))
            {
                throw new PresetException(PresetError.InvalidProtocol, protocol);
            }
        }

        if (rule.Process.Any(string.IsNullOrWhiteSpace))
        {
            throw new PresetException(PresetError.EmptyRule);
        }
    }

    private static void ValidateDomain(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace))
        {
            throw new PresetException(PresetError.InvalidDomain, value);
        }

        var prefix = DomainPrefixes.FirstOrDefault(p => value.StartsWith(p, StringComparison.Ordinal));
        if (prefix is null)
        {
            // Plain value: substring match; must not look like an unknown "xxx:" prefix.
            if (value.Contains(':', StringComparison.Ordinal))
            {
                throw new PresetException(PresetError.InvalidDomain, value);
            }

            return;
        }

        var body = value[prefix.Length..];
        if (body.Length == 0)
        {
            throw new PresetException(PresetError.InvalidDomain, value);
        }

        if (prefix == "regexp:")
        {
            try
            {
                _ = new Regex(body, RegexOptions.None, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException)
            {
                throw new PresetException(PresetError.InvalidDomain, value);
            }
        }
    }

    private static void ValidateIp(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace))
        {
            throw new PresetException(PresetError.InvalidIp, value);
        }

        if (value.StartsWith("geoip:", StringComparison.Ordinal) || value.StartsWith("ext:", StringComparison.Ordinal))
        {
            if (value.EndsWith(':'))
            {
                throw new PresetException(PresetError.InvalidIp, value);
            }

            return;
        }

        // Xray also accepts "!geoip:xx"; keep it for advanced users.
        if (value.StartsWith("!geoip:", StringComparison.Ordinal) && value.Length > "!geoip:".Length)
        {
            return;
        }

        if (!IsIpOrCidr(value))
        {
            throw new PresetException(PresetError.InvalidIp, value);
        }
    }

    /// <summary>An address or CIDR; host bits may be set (Xray accepts <c>192.168.1.1/24</c>).</summary>
    private static bool IsIpOrCidr(string value)
    {
        var slash = value.IndexOf('/', StringComparison.Ordinal);
        var addressPart = slash < 0 ? value : value[..slash];

        // IPAddress.TryParse accepts shorthand like "1" (= 0.0.0.1); require a full form.
        if (!IPAddress.TryParse(addressPart, out var address) ||
            (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && addressPart.Count(c => c == '.') != 3))
        {
            return false;
        }

        if (slash < 0)
        {
            return true;
        }

        var maxPrefix = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        var prefix = value[(slash + 1)..];
        return prefix.Length is > 0 and <= 3 && prefix.All(char.IsAsciiDigit) &&
            int.Parse(prefix, System.Globalization.CultureInfo.InvariantCulture) <= maxPrefix;
    }

    public static bool IsValidPortList(string value)
    {
        foreach (var part in value.Split(','))
        {
            var range = part.Trim().Split('-');
            if (range.Length is < 1 or > 2 || !range.All(IsPort))
            {
                return false;
            }

            if (range.Length == 2 && int.Parse(range[0], System.Globalization.CultureInfo.InvariantCulture) > int.Parse(range[1], System.Globalization.CultureInfo.InvariantCulture))
            {
                return false;
            }
        }

        return true;

        static bool IsPort(string s) =>
            s.Length is > 0 and <= 5 && s.All(char.IsAsciiDigit) &&
            int.Parse(s, System.Globalization.CultureInfo.InvariantCulture) is >= 1 and <= 65535;
    }
}
