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

    /// <summary>Raised after <see cref="Save"/>. May be raised on any thread.</summary>
    public event EventHandler? Changed;

    public AppSettings Settings
    {
        get
        {
            lock (_lock)
            {
                return _settings ??= Load();
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

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Applies <paramref name="change"/> to the current settings and saves the result.</summary>
    public AppSettings Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        AppSettings updated;
        lock (_lock)
        {
            updated = change(_settings ??= Load());
            JsonFile.Write(_paths.SettingsFile, updated, CoreJsonContext.Default.AppSettings);
            _settings = updated;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return updated;
    }

    private AppSettings Load() =>
        JsonFile.Read(_paths.SettingsFile, CoreJsonContext.Default.AppSettings) ?? new AppSettings();

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
