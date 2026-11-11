using SimpleVpn.Core.Storage;

namespace SimpleVpn.Core.Tests.Storage;

public sealed class AppPathsTests
{
    private static readonly string AppDir = Path.Combine("C:", "Apps", "SimpleVpn");
    private static readonly string Roaming = Path.Combine("C:", "Users", "u", "AppData", "Roaming");
    private static readonly string Local = Path.Combine("C:", "Users", "u", "AppData", "Local");

    [Fact]
    public void Resolve_WithoutPortableMarker_UsesUserProfileFolders()
    {
        var paths = AppPaths.Resolve(AppDir, Roaming, Local, _ => false);

        Assert.False(paths.IsPortable);
        Assert.Equal(Path.Combine(Roaming, AppPaths.AppFolderName), paths.ConfigDirectory);
        Assert.Equal(Path.Combine(Local, AppPaths.AppFolderName), paths.DataDirectory);
        Assert.Equal(Path.Combine(AppDir, "core"), paths.CoreDirectory);
        Assert.Equal(Path.Combine(Local, AppPaths.AppFolderName, "geo"), paths.GeoDirectory);
        Assert.Equal(Path.Combine(Roaming, AppPaths.AppFolderName, "settings.json"), paths.SettingsFile);
    }

    [Fact]
    public void Resolve_WithPortableMarker_StoresEverythingNextToExecutable()
    {
        var marker = Path.Combine(AppDir, AppPaths.PortableMarkerFileName);

        var paths = AppPaths.Resolve(AppDir, Roaming, Local, p => p == marker);

        Assert.True(paths.IsPortable);
        Assert.Equal(Path.Combine(AppDir, "data"), paths.ConfigDirectory);
        Assert.Equal(Path.Combine(AppDir, "data"), paths.DataDirectory);
    }

    [Fact]
    public void EnsureCreated_CreatesWritableDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), "SimpleVpnTests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = AppPaths.Resolve(root, Path.Combine(root, "roaming"), Path.Combine(root, "local"), _ => false);

            paths.EnsureCreated();

            Assert.True(Directory.Exists(paths.PresetsDirectory));
            Assert.True(Directory.Exists(paths.GeoDirectory));
            Assert.True(Directory.Exists(paths.RuntimeDirectory));
            Assert.True(Directory.Exists(paths.LogsDirectory));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
