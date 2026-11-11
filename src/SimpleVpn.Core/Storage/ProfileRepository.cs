using SimpleVpn.Core.Models;

namespace SimpleVpn.Core.Storage;

/// <summary>Server profiles (profiles.json) and the currently selected one.</summary>
public sealed class ProfileRepository
{
    private readonly AppPaths _paths;
    private readonly Lock _lock = new();
    private List<ServerProfile> _profiles;
    private Guid? _selectedId;

    public ProfileRepository(AppPaths paths)
    {
        _paths = paths;
        var document = JsonFile.Read(paths.ProfilesFile, CoreJsonContext.Default.ProfilesDocument) ?? new ProfilesDocument();
        _profiles = [.. document.Profiles];
        _selectedId = document.SelectedId;
        if (_selectedId is not null && _profiles.All(p => p.Id != _selectedId))
        {
            _selectedId = _profiles.FirstOrDefault()?.Id;
        }
    }

    /// <summary>Raised after any change (add, remove, select). May be raised on any thread.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<ServerProfile> Profiles
    {
        get
        {
            lock (_lock)
            {
                return [.. _profiles];
            }
        }
    }

    public ServerProfile? Selected
    {
        get
        {
            lock (_lock)
            {
                return _profiles.FirstOrDefault(p => p.Id == _selectedId);
            }
        }
    }

    /// <summary>
    /// Adds a profile and selects it. A profile for the same server and user
    /// (address, port, user id) is replaced instead of duplicated, keeping its id.
    /// </summary>
    public ServerProfile AddOrUpdate(ServerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        lock (_lock)
        {
            var index = _profiles.FindIndex(p =>
                string.Equals(p.Address, profile.Address, StringComparison.OrdinalIgnoreCase) &&
                p.Port == profile.Port &&
                string.Equals(p.UserId, profile.UserId, StringComparison.OrdinalIgnoreCase));

            if (index >= 0)
            {
                profile = profile with { Id = _profiles[index].Id };
                _profiles[index] = profile;
            }
            else
            {
                _profiles.Add(profile);
            }

            _selectedId = profile.Id;
            SaveLocked();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return profile;
    }

    public void Remove(Guid id)
    {
        lock (_lock)
        {
            if (_profiles.RemoveAll(p => p.Id == id) == 0)
            {
                return;
            }

            if (_selectedId == id)
            {
                _selectedId = _profiles.FirstOrDefault()?.Id;
            }

            SaveLocked();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Changes the display name only; the active server stays as it is.</summary>
    public void Rename(Guid id, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_lock)
        {
            var index = _profiles.FindIndex(p => p.Id == id);
            if (index < 0 || _profiles[index].Name == name)
            {
                return;
            }

            _profiles[index] = _profiles[index] with { Name = name };
            SaveLocked();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stores the resolved country of a server (no change of the active server).</summary>
    public void SetCountry(Guid id, string countryCode)
    {
        lock (_lock)
        {
            var index = _profiles.FindIndex(p => p.Id == id);
            if (index < 0 || _profiles[index].CountryCode == countryCode)
            {
                return;
            }

            _profiles[index] = _profiles[index] with { CountryCode = countryCode };
            SaveLocked();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Select(Guid id)
    {
        lock (_lock)
        {
            if (_selectedId == id || _profiles.All(p => p.Id != id))
            {
                return;
            }

            _selectedId = id;
            SaveLocked();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void SaveLocked() =>
        JsonFile.Write(
            _paths.ProfilesFile,
            new ProfilesDocument { SelectedId = _selectedId, Profiles = [.. _profiles] },
            CoreJsonContext.Default.ProfilesDocument);
}
