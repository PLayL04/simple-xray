using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleVpn.App.Localization;
using SimpleVpn.App.Services;
using SimpleVpn.Core.Storage;

namespace SimpleVpn.App.ViewModels;

/// <summary>
/// The app log inside the settings (the end of logs\simplevpn.log, or of the previous run's simplevpn.1.log),
/// so a friend can copy it and send it to the admin. Access keys never get into the log (see FileLoggerProvider callers).
/// </summary>
public sealed partial class LogViewerViewModel : ViewModelBase
{
    public const int MaxLines = 500;
    private const int TailBytes = 512 * 1024;

    private readonly AppPaths _paths;
    private readonly IClipboardService _clipboard;
    private readonly IFileService _files;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _text = string.Empty;

    /// <summary>Show simplevpn.1.log: useful right after a crash, when the current file belongs to the new run.</summary>
    [ObservableProperty]
    private bool _showPrevious;

    [ObservableProperty]
    private string? _message;

    public LogViewerViewModel(AppPaths paths, IClipboardService clipboard, IFileService files)
    {
        _paths = paths;
        _clipboard = clipboard;
        _files = files;
    }

    private string CurrentFile => Path.Combine(_paths.LogsDirectory, ShowPrevious ? "simplevpn.1.log" : "simplevpn.log");

    [RelayCommand]
    private void Open()
    {
        ShowPrevious = false;
        Load();
        IsOpen = true;
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    [RelayCommand]
    private void Refresh() => Load();

    [RelayCommand]
    private async Task CopyAsync()
    {
        await _clipboard.SetTextAsync(Text);
        Message = Loc.Get("Log_Copied");
    }

    [RelayCommand]
    private Task OpenFolderAsync() => _files.OpenFolderAsync(_paths.LogsDirectory);

    partial void OnShowPreviousChanged(bool value) => Load();

    private void Load()
    {
        Message = null;
        try
        {
            var text = ReadTail(CurrentFile, MaxLines);
            Text = text.Length > 0 ? text : Loc.Get("Log_Empty");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Text = Loc.Get("Log_ReadError");
        }
    }

    /// <summary>Last <paramref name="maxLines"/> lines of a file that the logger keeps open for writing.</summary>
    public static string ReadTail(string path, int maxLines)
    {
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(0, stream.Length - TailBytes);
        stream.Position = start;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lines = reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        // Started in the middle of a line: drop the fragment.
        if (start > 0 && lines.Count > 0)
        {
            lines.RemoveAt(0);
        }

        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return string.Join(Environment.NewLine, lines.TakeLast(maxLines));
    }
}
