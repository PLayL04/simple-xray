using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace SimpleVpn.App.Services;

/// <summary>File pickers and opening folders (platform dialogs).</summary>
public interface IFileService
{
    /// <summary>Lets the user pick a JSON file and returns its text, or null when cancelled.</summary>
    Task<string?> OpenJsonFileAsync(string title, int maxBytes);

    /// <summary>Lets the user choose where to save a JSON file. Returns false when cancelled.</summary>
    Task<bool> SaveJsonFileAsync(string title, string suggestedFileName, string content);

    Task OpenFolderAsync(string path);
}

public sealed class AvaloniaFileService : IFileService
{
    private static readonly FilePickerFileType[] JsonTypes =
        [new("JSON") { Patterns = ["*.json"], MimeTypes = ["application/json"] }];

    public async Task<string?> OpenJsonFileAsync(string title, int maxBytes)
    {
        var storage = TopLevel()?.StorageProvider;
        if (storage is null)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false, FileTypeFilter = JsonTypes });
        if (files.Count == 0)
        {
            return null;
        }

        var properties = await files[0].GetBasicPropertiesAsync();
        if (properties.Size > (ulong)maxBytes)
        {
            throw new IOException("File is too large");
        }

        await using var stream = await files[0].OpenReadAsync();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    public async Task<bool> SaveJsonFileAsync(string title, string suggestedFileName, string content)
    {
        var storage = TopLevel()?.StorageProvider;
        if (storage is null)
        {
            return false;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "json",
            FileTypeChoices = JsonTypes,
        });
        if (file is null)
        {
            return false;
        }

        await using var stream = await file.OpenWriteAsync();
        stream.SetLength(0);
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(content);
        return true;
    }

    public async Task OpenFolderAsync(string path)
    {
        if (TopLevel()?.Launcher is { } launcher)
        {
            Directory.CreateDirectory(path);
            await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
        }
    }

    private static TopLevel? TopLevel() => Application.Current?.ApplicationLifetime switch
    {
        IClassicDesktopStyleApplicationLifetime desktop => desktop.MainWindow,
        ISingleViewApplicationLifetime single => Avalonia.Controls.TopLevel.GetTopLevel(single.MainView),
        _ => null,
    };
}
