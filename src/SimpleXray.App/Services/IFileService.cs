using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using SimpleXray.Core.Qr;

namespace SimpleXray.App.Services;

/// <summary>File pickers and opening folders (platform dialogs).</summary>
public interface IFileService
{
    /// <summary>Lets the user pick a JSON file and returns its text, or null when cancelled.</summary>
    Task<string?> OpenJsonFileAsync(string title, int maxBytes);

    /// <summary>Lets the user choose where to save a JSON file. Returns false when cancelled.</summary>
    Task<bool> SaveJsonFileAsync(string title, string suggestedFileName, string content);

    Task OpenFolderAsync(string path);

    /// <summary>
    /// Lets the user pick a picture and decodes it to pixels, or returns null when cancelled.
    /// Throws <see cref="IOException"/> for files that are too large or not images.
    /// </summary>
    Task<RawImage?> OpenImageAsync(string title, int maxBytes);
}

public sealed class AvaloniaFileService : IFileService
{
    private static readonly FilePickerFileType[] JsonTypes =
        [new("JSON") { Patterns = ["*.json"], MimeTypes = ["application/json"] }];

    private static readonly FilePickerFileType[] ImageTypes =
        [FilePickerFileTypes.ImageAll, new("*") { Patterns = ["*"] }];

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

    public async Task<RawImage?> OpenImageAsync(string title, int maxBytes)
    {
        var storage = TopLevel()?.StorageProvider;
        if (storage is null)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false, FileTypeFilter = ImageTypes });
        if (files.Count == 0)
        {
            return null;
        }

        var properties = await files[0].GetBasicPropertiesAsync();
        if (properties.Size > (ulong)maxBytes)
        {
            throw new IOException("File is too large");
        }

        var buffer = new MemoryStream();
        await using (var stream = await files[0].OpenReadAsync())
        {
            await stream.CopyToAsync(buffer);
        }

        buffer.Position = 0;
        return await Task.Run(() => Decode(buffer));
    }

    /// <summary>Decodes PNG/JPEG/BMP/GIF/WebP into 32-bit pixels (channel order does not matter for QR).</summary>
    public static RawImage Decode(Stream stream)
    {
        Bitmap bitmap;
        try
        {
            bitmap = new Bitmap(stream);
        }
#pragma warning disable CA1031 // The decoder throws various exception types for broken files.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            throw new IOException("Not an image", ex);
        }

        using (bitmap)
        {
            var size = bitmap.PixelSize;
            if ((long)size.Width * size.Height > RawImage.MaxPixels)
            {
                throw new IOException("Image is too large");
            }

            if (bitmap.Format is { } format && format != PixelFormats.Bgra8888 && format != PixelFormats.Rgba8888)
            {
                throw new IOException($"Unsupported pixel format {format}");
            }

            var stride = size.Width * 4;
            var pixels = new byte[stride * size.Height];
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                bitmap.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), pixels.Length, stride);
            }
            finally
            {
                handle.Free();
            }

            return new RawImage(pixels, size.Width, size.Height);
        }
    }

    private static TopLevel? TopLevel() => Application.Current?.ApplicationLifetime switch
    {
        IClassicDesktopStyleApplicationLifetime desktop => desktop.MainWindow,
        ISingleViewApplicationLifetime single => Avalonia.Controls.TopLevel.GetTopLevel(single.MainView),
        _ => null,
    };
}
