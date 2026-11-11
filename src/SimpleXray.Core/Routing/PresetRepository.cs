using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using SimpleXray.Core.Storage;

namespace SimpleXray.Core.Routing;

/// <summary>
/// Built-in presets plus user presets (<c>presets\&lt;id&gt;.json</c>) and the active one
/// (<see cref="Models.AppSettings.SelectedPresetId"/>).
/// </summary>
public sealed partial class PresetRepository
{
    private const string UserIdPrefix = "user-";

    private readonly AppPaths _paths;
    private readonly SettingsStore _settings;
    private readonly ILogger<PresetRepository> _logger;
    private readonly Lock _lock = new();
    private List<RoutingPreset> _userPresets;

    public PresetRepository(AppPaths paths, SettingsStore settings, ILogger<PresetRepository> logger)
    {
        _paths = paths;
        _settings = settings;
        _logger = logger;
        _userPresets = LoadUserPresets();
    }

    /// <summary>Raised after any change (save, delete, select). May be raised on any thread.</summary>
    public event EventHandler? Changed;

    /// <summary>Built-in presets first (fixed order), then user presets by name.</summary>
    public IReadOnlyList<RoutingPreset> Presets
    {
        get
        {
            lock (_lock)
            {
                return [.. BuiltInPresets.All, .. _userPresets.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)];
            }
        }
    }

    /// <summary>The active preset; falls back to <see cref="BuiltInPresets.Default"/> when the saved id is unknown.</summary>
    public RoutingPreset Selected => Find(_settings.Settings.SelectedPresetId) ?? BuiltInPresets.Default;

    public RoutingPreset? Find(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        lock (_lock)
        {
            return BuiltInPresets.All.FirstOrDefault(p => p.Id == id) ?? _userPresets.FirstOrDefault(p => p.Id == id);
        }
    }

    public void Select(string id)
    {
        var preset = Find(id) ?? throw new PresetException(PresetError.NotFound, id);
        if (_settings.Settings.SelectedPresetId == preset.Id)
        {
            return;
        }

        _settings.Update(s => s with { SelectedPresetId = preset.Id });
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Creates an editable copy of any preset.</summary>
    public RoutingPreset Duplicate(string id, string name)
    {
        var source = Find(id) ?? throw new PresetException(PresetError.NotFound, id);
        return SaveNew(source with { Name = name, Names = null, SourceUrl = null });
    }

    /// <summary>Saves changes to an existing user preset (validated). Built-in presets are read-only.</summary>
    public RoutingPreset Save(RoutingPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (BuiltInPresets.IsBuiltInId(preset.Id))
        {
            throw new PresetException(PresetError.BuiltInReadOnly, preset.Id);
        }

        preset = PresetSerializer.Normalize(preset) with { BuiltIn = false, SchemaVersion = RoutingPreset.CurrentSchemaVersion };
        PresetValidator.Validate(preset);

        lock (_lock)
        {
            var index = _userPresets.FindIndex(p => p.Id == preset.Id);
            if (index < 0)
            {
                throw new PresetException(PresetError.NotFound, preset.Id);
            }

            WriteFile(preset);
            _userPresets[index] = preset;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return preset;
    }

    /// <summary>Imports preset JSON as a new user preset. The id in the file is ignored.</summary>
    public RoutingPreset Import(string json, string? sourceUrl = null)
    {
        var preset = PresetSerializer.Parse(json);
        return SaveNew(preset with { SourceUrl = sourceUrl ?? preset.SourceUrl });
    }

    /// <summary>Replaces the rules of a user preset with a freshly downloaded version, keeping its id and name.</summary>
    public RoutingPreset ReplaceFromSource(string id, string json)
    {
        var current = Find(id) ?? throw new PresetException(PresetError.NotFound, id);
        var downloaded = PresetSerializer.Parse(json);
        return Save(downloaded with { Id = current.Id, Name = current.Name, Names = null, SourceUrl = current.SourceUrl });
    }

    public void Delete(string id)
    {
        if (BuiltInPresets.IsBuiltInId(id))
        {
            throw new PresetException(PresetError.BuiltInReadOnly, id);
        }

        lock (_lock)
        {
            if (_userPresets.RemoveAll(p => p.Id == id) == 0)
            {
                return;
            }

            JsonFile.Delete(FilePath(id));
        }

        if (_settings.Settings.SelectedPresetId == id)
        {
            _settings.Update(s => s with { SelectedPresetId = BuiltInPresets.DefaultId });
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public static string Export(RoutingPreset preset) =>
        PresetSerializer.ToJson(PresetSerializer.Normalize(preset) with { SchemaVersion = RoutingPreset.CurrentSchemaVersion });

    private RoutingPreset SaveNew(RoutingPreset preset)
    {
        preset = PresetSerializer.Normalize(preset) with
        {
            Id = NewId(),
            BuiltIn = false,
            SchemaVersion = RoutingPreset.CurrentSchemaVersion,
        };

        // Imported built-in style presets carry localized names only; keep one plain name for editing.
        if (string.IsNullOrWhiteSpace(preset.Name))
        {
            preset = preset with { Name = preset.DisplayName(CultureInfo.CurrentUICulture) };
        }

        PresetValidator.Validate(preset);

        lock (_lock)
        {
            WriteFile(preset);
            _userPresets.Add(preset);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return preset;
    }

    private static string NewId() => UserIdPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));

    private string FilePath(string id) => Path.Combine(_paths.PresetsDirectory, id + ".json");

    private void WriteFile(RoutingPreset preset)
    {
        Directory.CreateDirectory(_paths.PresetsDirectory);
        var path = FilePath(preset.Id);
        var temp = path + ".tmp";
        File.WriteAllText(temp, PresetSerializer.ToJson(preset));
        File.Move(temp, path, overwrite: true);
    }

    private List<RoutingPreset> LoadUserPresets()
    {
        var result = new List<RoutingPreset>();
        if (!Directory.Exists(_paths.PresetsDirectory))
        {
            return result;
        }

        foreach (var file in Directory.EnumerateFiles(_paths.PresetsDirectory, "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            try
            {
                if (new FileInfo(file).Length > PresetSerializer.MaxSizeBytes)
                {
                    throw new PresetException(PresetError.TooLarge);
                }

                // The file name is the id: users may copy preset files into the folder by hand.
                var preset = PresetSerializer.Parse(File.ReadAllText(file)) with { Id = id, BuiltIn = false };
                if (BuiltInPresets.IsBuiltInId(id))
                {
                    LogSkipped(_logger, file, "id of a built-in preset");
                    continue;
                }

                result.Add(preset);
            }
            catch (PresetException ex)
            {
                LogSkipped(_logger, file, ex.Message);
            }
            catch (IOException ex)
            {
                LogSkipped(_logger, file, ex.Message);
            }
        }

        return result;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping preset file {File}: {Reason}")]
    private static partial void LogSkipped(ILogger logger, string file, string reason);
}
