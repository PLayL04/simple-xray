using System.Runtime.InteropServices;

namespace SimpleVpn.Desktop.Platform;

/// <summary>
/// One running instance per Windows user session. A second instance must not run: at startup it would treat
/// the first instance's applied system proxy as a crash leftover and reset it, and if connected it would save
/// the first instance's proxy as the "previous" one and later restore a dead port.
/// The second start only asks the first instance to show its window, then exits.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\SimpleVpn.SingleInstance";
    private const string ActivateEventName = @"Local\SimpleVpn.Activate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle? _activate;
    private readonly RegisteredWaitHandle? _registration;

    private SingleInstance(Mutex mutex, EventWaitHandle? activate, Action? onActivate)
    {
        _mutex = mutex;
        _activate = activate;
        if (activate is not null && onActivate is not null)
        {
            _registration = ThreadPool.RegisterWaitForSingleObject(activate, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);
        }
    }

    public bool IsFirst => _activate is not null;

    /// <summary>
    /// Claims the instance. When another instance already runs, signals it to activate and returns an object with
    /// <see cref="IsFirst"/> = false; <paramref name="onActivate"/> is called (on a pool thread) for later starts.
    /// </summary>
    /// <param name="waitForPrevious">
    /// Elevated restart: the previous (non-elevated) instance is shutting down, so wait for it instead of deferring to it.
    /// </param>
    public static SingleInstance Claim(Action onActivate, bool waitForPrevious = false)
    {
        Mutex mutex;
        bool createdNew;
        try
        {
            mutex = new Mutex(initiallyOwned: true, MutexName, out createdNew);
        }
        catch (UnauthorizedAccessException)
        {
            // Held by an elevated instance whose objects a normal one may not open: that instance is running.
            return new SingleInstance(new Mutex(), activate: null, onActivate: null);
        }

        if (!createdNew && waitForPrevious)
        {
            try
            {
                createdNew = mutex.WaitOne(TimeSpan.FromSeconds(15));
            }
            catch (AbandonedMutexException)
            {
                createdNew = true;
            }
        }

        if (!createdNew)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var existing))
                {
                    using (existing)
                    {
                        // We are the foreground process (the user just started us): let the first instance take focus.
                        NativeMethods.AllowSetForegroundWindow(NativeMethods.AsfwAny);
                        existing.Set();
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
            }

            return new SingleInstance(mutex, activate: null, onActivate: null);
        }

        var activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        return new SingleInstance(mutex, activate, onActivate);
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _activate?.Dispose();
        if (IsFirst)
        {
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
    }

    private static class NativeMethods
    {
        public const int AsfwAny = -1;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AllowSetForegroundWindow(int processId);
    }
}
