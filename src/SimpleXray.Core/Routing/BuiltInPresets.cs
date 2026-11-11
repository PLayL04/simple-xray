namespace SimpleXray.Core.Routing;

/// <summary>Presets shipped inside the assembly (<c>Routing/Presets/*.json</c>). Immutable; can only be duplicated.</summary>
public static class BuiltInPresets
{
    public const string GlobalId = "global";
    public const string BypassRuId = "bypass-ru";
    public const string BlockedOnlyId = "blocked-only";

    /// <summary>Preset selected on first start.</summary>
    public const string DefaultId = BypassRuId;

    private static readonly Lazy<IReadOnlyList<RoutingPreset>> Presets = new(Load);

    /// <summary>In display order.</summary>
    public static IReadOnlyList<RoutingPreset> All => Presets.Value;

    public static RoutingPreset Global => Get(GlobalId);

    public static RoutingPreset Default => Get(DefaultId);

    public static bool IsBuiltInId(string id) => All.Any(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    public static RoutingPreset Get(string id) => All.First(p => p.Id == id);

    private static IReadOnlyList<RoutingPreset> Load()
    {
        var assembly = typeof(BuiltInPresets).Assembly;
        return [.. new[] { GlobalId, BypassRuId, BlockedOnlyId }.Select(id =>
        {
            var resource = $"SimpleXray.Core.Routing.Presets.{id}.json";
            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Embedded preset '{resource}' is missing.");
            using var reader = new StreamReader(stream);
            var preset = PresetSerializer.Parse(reader.ReadToEnd());
            return preset.Id == id
                ? preset with { BuiltIn = true }
                : throw new InvalidOperationException($"Embedded preset '{resource}' has id '{preset.Id}'.");
        })];
    }
}
