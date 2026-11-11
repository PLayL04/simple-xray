using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using SimpleXray.Core.Abstractions;

namespace SimpleXray.Desktop.Platform;

/// <summary>
/// The app runs as <c>asInvoker</c>; VPN (TUN) mode restarts it through UAC (<c>runas</c>) with
/// <see cref="ConnectArgument"/> so the elevated instance connects right away.
/// </summary>
internal sealed class WindowsElevation : IElevation
{
    /// <summary>Connect as soon as the window is shown.</summary>
    public const string ConnectArgument = "--connect";

    /// <summary>Started by <see cref="RestartElevated"/>: wait for the previous instance to exit instead of deferring to it.</summary>
    public const string RestartArgument = "--elevated-restart";

    private const int ErrorCancelled = 1223;

    private static readonly Lazy<bool> Elevated = new(() =>
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    });

    private readonly string[] _forwardedArgs;

    /// <param name="args">Command line of this instance; options like <c>--lang</c> are passed on.</param>
    public WindowsElevation(string[] args)
    {
        _forwardedArgs = [.. args.Where(a => a != ConnectArgument && a != RestartArgument)];
    }

    public bool IsElevated => Elevated.Value;

    public bool RestartElevated()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Process path is unknown.");
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach (var arg in _forwardedArgs.Append(ConnectArgument).Append(RestartArgument))
        {
            psi.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(psi);
            return process is not null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            // The user said "No" in the UAC prompt.
            return false;
        }
    }
}
