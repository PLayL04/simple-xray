using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace SimpleVpn.Core.Storage;

/// <summary>Atomic JSON file persistence.</summary>
internal static class JsonFile
{
    /// <summary>
    /// Reads <paramref name="path"/>. Returns null when the file is missing.
    /// A corrupt file is renamed to <c>*.corrupt</c> (kept for diagnostics) and null is returned.
    /// </summary>
    public static T? Read<T>(string path, JsonTypeInfo<T> typeInfo)
        where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, typeInfo);
        }
        catch (JsonException)
        {
            File.Move(path, path + ".corrupt", overwrite: true);
            return null;
        }
    }

    /// <summary>Writes to a temporary file first and then replaces the target, so a crash never leaves a half-written file.</summary>
    public static void Write<T>(string path, T value, JsonTypeInfo<T> typeInfo)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        using (var stream = File.Create(temp))
        {
            JsonSerializer.Serialize(stream, value, typeInfo);
        }

        File.Move(temp, path, overwrite: true);
    }

    public static void Delete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
