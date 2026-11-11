using SimpleVpn.Core.Models;

namespace SimpleVpn.Core.Storage;

/// <summary>settings.json and state.json.</summary>
public sealed class SettingsStore
{
    private readonly AppPaths _paths;
    private readonly Lock _lock = new();
    private AppSettings? _settings;

    public SettingsStore(AppPaths paths)
    {
        _paths = paths;
    }

    public AppSettings Settings
    {
        get
        {
            lock (_lock)
            {
                return _settings ??= JsonFile.Read(_paths.SettingsFile, CoreJsonContext.Default.AppSettings) ?? new AppSettings();
            }
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_lock)
        {
            JsonFile.Write(_paths.SettingsFile, settings, CoreJsonContext.Default.AppSettings);
            _settings = settings;
        }
    }

    public RuntimeState LoadState() =>
        JsonFile.Read(_paths.StateFile, CoreJsonContext.Default.RuntimeState) ?? new RuntimeState();

    public void SaveState(RuntimeState state)
    {
        if (state.ProxyApplied)
        {
            JsonFile.Write(_paths.StateFile, state, CoreJsonContext.Default.RuntimeState);
        }
        else
        {
            JsonFile.Delete(_paths.StateFile);
        }
    }
}
