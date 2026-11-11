using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace SimpleXray.App.Services;

/// <summary>Ends the application (e.g. after starting an elevated copy).</summary>
public interface IAppLifetime
{
    void Shutdown();
}

public sealed class AvaloniaAppLifetime : IAppLifetime
{
    public void Shutdown()
    {
        if (Application.Current?.ApplicationLifetime is IControlledApplicationLifetime lifetime)
        {
            lifetime.Shutdown();
        }
    }
}
