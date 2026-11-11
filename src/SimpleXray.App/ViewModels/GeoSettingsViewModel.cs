using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleXray.App.Localization;
using SimpleXray.App.Services;
using SimpleXray.Core.Geo;
using SimpleXray.Core.Models;
using SimpleXray.Core.Net;
using SimpleXray.Core.Storage;

namespace SimpleXray.App.ViewModels;

/// <summary>"Geo databases" section of the settings: status, update now, auto-update interval, source.</summary>
public sealed partial class GeoSettingsViewModel : ViewModelBase, IDisposable
{
    private readonly GeoUpdater _updater;
    private readonly SettingsStore _settings;
    private readonly IUiDispatcher _ui;
    private bool _syncing;
    private GeoUpdateOutcome? _lastOutcome;

    [ObservableProperty]
    private bool _autoUpdate;

    /// <summary>Index in <see cref="GeoSettings.IntervalChoicesHours"/>.</summary>
    [ObservableProperty]
    private int _intervalIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseDefaultSource))]
    private bool _useCustomSource;

    [ObservableProperty]
    private string _customGeoipUrl = string.Empty;

    [ObservableProperty]
    private string _customGeositeUrl = string.Empty;

    [ObservableProperty]
    private string? _sourceMessage;

    [ObservableProperty]
    private bool _isSourceMessageError;

    public GeoSettingsViewModel(GeoUpdater updater, SettingsStore settings, IUiDispatcher ui)
    {
        _updater = updater;
        _settings = settings;
        _ui = ui;
        _updater.StatusChanged += OnStatusChanged;
        Refresh();
    }

    public bool UseDefaultSource
    {
        get => !UseCustomSource;
        set => UseCustomSource = !value;
    }

    public bool IsUpdating => _updater.Status.IsUpdating;

    public bool IsIdle => !IsUpdating;

    public bool HasProgress => _updater.Status.Progress is not null;

    public double ProgressPercent => (_updater.Status.Progress ?? 0) * 100;

    public string LastUpdateText => _updater.Status.LastUpdateUtc is { } updated
        ? Loc.Format("Geo_LastUpdate", updated.ToLocalTime().ToString("g", Loc.Culture))
        : Loc.Get("Geo_NeverUpdated");

    public string? LastCheckText => _updater.Status.LastCheckUtc is { } checkedAt
        ? Loc.Format("Geo_LastCheck", checkedAt.ToLocalTime().ToString("g", Loc.Culture))
        : null;

    /// <summary>Progress while updating, otherwise the result of the last check or its error.</summary>
    public string? ResultText
    {
        get
        {
            var status = _updater.Status;
            if (status.IsUpdating)
            {
                return status.Progress is { } p ? Loc.Format("Geo_UpdatingPercent", (int)(p * 100)) : Loc.Get("Geo_Updating");
            }

            if (status.LastError is { } error)
            {
                return ErrorText(error, status.LastErrorDetail);
            }

            return _lastOutcome switch
            {
                GeoUpdateOutcome.UpToDate => Loc.Get("Geo_UpToDate"),
                GeoUpdateOutcome.Updated => Loc.Get("Geo_Updated"),
                _ => null,
            };
        }
    }

    public bool IsError => !IsUpdating && _updater.Status.LastError is not null;

    public static string ErrorText(GeoUpdateError error, string? detail)
    {
        var text = Loc.Get($"GeoError_{error}");
        return detail is { Length: > 0 } && error is GeoUpdateError.MissingCategory or GeoUpdateError.DownloadFailed
            ? $"{text}: {detail}"
            : text;
    }

    public void Refresh()
    {
        var geo = _settings.Settings.Geo;
        _syncing = true;
        try
        {
            AutoUpdate = geo.AutoUpdate;
            var index = GeoSettings.IntervalChoicesHours.ToList().IndexOf(geo.IntervalHours);
            IntervalIndex = index >= 0 ? index : GeoSettings.IntervalChoicesHours.ToList().IndexOf(24);
            UseCustomSource = geo.UseCustomSource;
            CustomGeoipUrl = geo.CustomGeoipUrl ?? string.Empty;
            CustomGeositeUrl = geo.CustomGeositeUrl ?? string.Empty;
            SourceMessage = null;
        }
        finally
        {
            _syncing = false;
        }

        RaiseStatus();
    }

    [RelayCommand]
    private async Task UpdateNowAsync()
    {
        if (IsUpdating)
        {
            return;
        }

        _lastOutcome = null;
        _lastOutcome = await _updater.UpdateAsync();
        RaiseStatus();
    }

    partial void OnAutoUpdateChanged(bool value)
    {
        if (!_syncing)
        {
            _settings.Update(s => s with { Geo = s.Geo with { AutoUpdate = value } });
        }
    }

    partial void OnIntervalIndexChanged(int value)
    {
        if (!_syncing && value >= 0 && value < GeoSettings.IntervalChoicesHours.Count)
        {
            _settings.Update(s => s with { Geo = s.Geo with { IntervalHours = GeoSettings.IntervalChoicesHours[value] } });
        }
    }

    partial void OnUseCustomSourceChanged(bool value)
    {
        SourceMessage = null;

        // Switching back to the default source applies at once; custom URLs are saved with the button.
        if (!_syncing && !value)
        {
            _settings.Update(s => s with { Geo = s.Geo with { UseCustomSource = false } });
        }
    }

    [RelayCommand]
    private void SaveSource()
    {
        if (!HttpDownloader.IsSupportedUrl(CustomGeoipUrl, out var geoip) || !HttpDownloader.IsSupportedUrl(CustomGeositeUrl, out var geosite))
        {
            SourceMessage = Loc.Get("Geo_InvalidUrls");
            IsSourceMessageError = true;
            return;
        }

        _settings.Update(s => s with
        {
            Geo = s.Geo with { UseCustomSource = true, CustomGeoipUrl = geoip!.ToString(), CustomGeositeUrl = geosite!.ToString() },
        });
        SourceMessage = Loc.Get("Geo_SourceSaved");
        IsSourceMessageError = false;
    }

    private void OnStatusChanged(object? sender, EventArgs e) => _ui.Post(RaiseStatus);

    private void RaiseStatus()
    {
        OnPropertyChanged(nameof(IsUpdating));
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(HasProgress));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(LastUpdateText));
        OnPropertyChanged(nameof(LastCheckText));
        OnPropertyChanged(nameof(ResultText));
        OnPropertyChanged(nameof(IsError));
    }

    public void Dispose() => _updater.StatusChanged -= OnStatusChanged;
}
