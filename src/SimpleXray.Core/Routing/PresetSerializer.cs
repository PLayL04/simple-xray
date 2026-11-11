using System.Text;
using System.Text.Json;
using SimpleXray.Core.Storage;

namespace SimpleXray.Core.Routing;

/// <summary>Preset JSON ⇄ <see cref="RoutingPreset"/> (the same format for built-ins, user files, import and export).</summary>
public static class PresetSerializer
{
    public const int MaxSizeBytes = 1024 * 1024;

    /// <summary>Parses, normalizes and validates a preset. Throws <see cref="PresetException"/>.</summary>
    public static RoutingPreset Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > MaxSizeBytes)
        {
            throw new PresetException(PresetError.TooLarge);
        }

        RoutingPreset? preset;
        try
        {
            preset = JsonSerializer.Deserialize(json, CoreJsonContext.Default.RoutingPreset);
        }
        catch (JsonException ex)
        {
            throw new PresetException(PresetError.Malformed, ex.Message);
        }

        if (preset is null)
        {
            throw new PresetException(PresetError.Malformed);
        }

        preset = Normalize(preset);
        PresetValidator.Validate(preset);
        return preset;
    }

    public static string ToJson(RoutingPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return JsonSerializer.Serialize(preset, CoreJsonContext.Default.RoutingPreset);
    }

    /// <summary>Trims values, drops empty entries and replaces JSON nulls with empty lists.</summary>
    public static RoutingPreset Normalize(RoutingPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return preset with
        {
            Id = preset.Id?.Trim() ?? string.Empty,
            Name = preset.Name?.Trim() ?? string.Empty,
            DomainStrategy = string.IsNullOrWhiteSpace(preset.DomainStrategy) ? "IPIfNonMatch" : preset.DomainStrategy.Trim(),
            SourceUrl = string.IsNullOrWhiteSpace(preset.SourceUrl) ? null : preset.SourceUrl.Trim(),
            Rules = [.. (preset.Rules ?? []).Where(r => r is not null).Select(NormalizeRule)],
        };
    }

    private static RoutingRule NormalizeRule(RoutingRule rule) => rule with
    {
        Name = string.IsNullOrWhiteSpace(rule.Name) ? null : rule.Name.Trim(),
        Domain = Clean(rule.Domain),
        Ip = Clean(rule.Ip),
        Port = string.IsNullOrWhiteSpace(rule.Port) ? null : rule.Port.Replace(" ", string.Empty, StringComparison.Ordinal),
        Network = string.IsNullOrWhiteSpace(rule.Network) ? null : rule.Network.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant(),
        Protocol = [.. Clean(rule.Protocol).Select(p => p.ToLowerInvariant())],
        Process = Clean(rule.Process),
    };

    private static string[] Clean(IReadOnlyList<string>? values) =>
        values is null ? [] : [.. values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Distinct(StringComparer.Ordinal)];
}
