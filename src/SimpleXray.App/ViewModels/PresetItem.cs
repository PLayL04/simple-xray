using SimpleXray.App.Localization;
using SimpleXray.Core.Routing;

namespace SimpleXray.App.ViewModels;

/// <summary>A preset in pickers and lists, with its name in the UI language.</summary>
public sealed record PresetItem(RoutingPreset Preset)
{
    public string Id => Preset.Id;

    public string Name => Preset.DisplayName(Loc.Culture);

    public bool IsBuiltIn => Preset.BuiltIn;

    /// <summary>The preset currently used for connections (shown with a marker in settings).</summary>
    public bool IsActive { get; init; }

    /// <summary>Second line in the settings list: "built-in" or the import source.</summary>
    public string Description => Preset.BuiltIn
        ? Loc.Get("Presets_BuiltIn")
        : Preset.SourceUrl is { } url ? Loc.Format("Presets_FromSource", url) : Loc.Get("Presets_Custom");

    public static string ErrorText(PresetException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var text = Loc.Get($"PresetError_{ex.Error}");
        if (ex.Detail is { Length: > 0 } detail && ex.Error is PresetError.InvalidDomain or PresetError.InvalidIp
                or PresetError.InvalidPort or PresetError.InvalidNetwork or PresetError.InvalidProtocol)
        {
            text = $"{text}: {detail}";
        }

        return ex.RuleNumber is { } number ? Loc.Format("PresetError_InRule", number, text) : text;
    }
}
