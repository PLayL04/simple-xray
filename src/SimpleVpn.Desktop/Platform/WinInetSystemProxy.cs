using System.ComponentModel;
using System.Runtime.InteropServices;
using SimpleVpn.Core.Abstractions;
using SimpleVpn.Core.Models;

namespace SimpleVpn.Desktop.Platform;

/// <summary>
/// Windows system proxy via the WinINet per-connection API (the same settings as
/// "Settings → Network → Proxy"). Applies to the LAN connection.
/// TODO: dial-up/RAS connections are not updated (rare today).
/// </summary>
internal sealed class WinInetSystemProxy : ISystemProxy
{
    private const int InternetOptionPerConnectionOption = 75;
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;
    private const int InternetOptionProxySettingsChanged = 95;

    private const int PerConnFlags = 1;
    private const int PerConnProxyServer = 2;
    private const int PerConnProxyBypass = 3;
    private const int PerConnAutoConfigUrl = 4;

    private const int ProxyTypeDirect = 0x1;
    private const int ProxyTypeProxy = 0x2;
    private const int ProxyTypeAutoProxyUrl = 0x4;
    private const int ProxyTypeAutoDetect = 0x8;

    public SystemProxySettings GetCurrent()
    {
        var options = new[]
        {
            new InternetPerConnOption { Option = PerConnFlags },
            new InternetPerConnOption { Option = PerConnProxyServer },
            new InternetPerConnOption { Option = PerConnProxyBypass },
            new InternetPerConnOption { Option = PerConnAutoConfigUrl },
        };

        var buffer = AllocOptions(options);
        try
        {
            var list = CreateList(buffer, options.Length);
            var size = Marshal.SizeOf<InternetPerConnOptionList>();
            if (!NativeMethods.InternetQueryOptionW(IntPtr.Zero, InternetOptionPerConnectionOption, ref list, ref size))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var result = ReadOptions(buffer, options.Length);
            var flags = unchecked((int)result[0].Value.ToInt64());
            return new SystemProxySettings
            {
                Enabled = (flags & ProxyTypeProxy) != 0,
                Server = TakeString(result[1].Value),
                Bypass = TakeString(result[2].Value),
                AutoConfigUrl = (flags & ProxyTypeAutoProxyUrl) != 0 ? TakeString(result[3].Value) : FreeAndNull(result[3].Value),
                AutoDetect = (flags & ProxyTypeAutoDetect) != 0,
            };
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Apply(SystemProxySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var flags = ProxyTypeDirect;
        if (settings.Enabled && !string.IsNullOrEmpty(settings.Server))
        {
            flags |= ProxyTypeProxy;
        }

        if (!string.IsNullOrEmpty(settings.AutoConfigUrl))
        {
            flags |= ProxyTypeAutoProxyUrl;
        }

        if (settings.AutoDetect)
        {
            flags |= ProxyTypeAutoDetect;
        }

        var strings = new List<IntPtr>();
        IntPtr Str(string? s)
        {
            var p = Marshal.StringToHGlobalUni(s ?? string.Empty);
            strings.Add(p);
            return p;
        }

        var options = new[]
        {
            new InternetPerConnOption { Option = PerConnFlags, Value = flags },
            new InternetPerConnOption { Option = PerConnProxyServer, Value = Str(settings.Server) },
            new InternetPerConnOption { Option = PerConnProxyBypass, Value = Str(settings.Bypass) },
            new InternetPerConnOption { Option = PerConnAutoConfigUrl, Value = Str(settings.AutoConfigUrl) },
        };

        var buffer = AllocOptions(options);
        try
        {
            var list = CreateList(buffer, options.Length);
            if (!NativeMethods.InternetSetOptionW(IntPtr.Zero, InternetOptionPerConnectionOption, ref list, Marshal.SizeOf<InternetPerConnOptionList>()))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            NativeMethods.InternetSetOptionW(IntPtr.Zero, InternetOptionProxySettingsChanged, IntPtr.Zero, 0);
            NativeMethods.InternetSetOptionW(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
            NativeMethods.InternetSetOptionW(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            strings.ForEach(Marshal.FreeHGlobal);
        }
    }

    private static IntPtr AllocOptions(InternetPerConnOption[] options)
    {
        var itemSize = Marshal.SizeOf<InternetPerConnOption>();
        var buffer = Marshal.AllocHGlobal(itemSize * options.Length);
        for (var i = 0; i < options.Length; i++)
        {
            Marshal.StructureToPtr(options[i], buffer + (i * itemSize), fDeleteOld: false);
        }

        return buffer;
    }

    private static InternetPerConnOption[] ReadOptions(IntPtr buffer, int count)
    {
        var itemSize = Marshal.SizeOf<InternetPerConnOption>();
        var result = new InternetPerConnOption[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = Marshal.PtrToStructure<InternetPerConnOption>(buffer + (i * itemSize));
        }

        return result;
    }

    private static InternetPerConnOptionList CreateList(IntPtr options, int count) => new()
    {
        Size = Marshal.SizeOf<InternetPerConnOptionList>(),
        Connection = IntPtr.Zero, // LAN
        OptionCount = count,
        OptionError = 0,
        Options = options,
    };

    /// <summary>Reads a string returned by InternetQueryOption and frees it (allocated with GlobalAlloc).</summary>
    private static string? TakeString(IntPtr value)
    {
        if (value == IntPtr.Zero)
        {
            return null;
        }

        var s = Marshal.PtrToStringUni(value);
        NativeMethods.GlobalFree(value);
        return string.IsNullOrEmpty(s) ? null : s;
    }

    private static string? FreeAndNull(IntPtr value)
    {
        TakeString(value);
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InternetPerConnOption
    {
        public int Option;

        // Union of DWORD / LPWSTR / FILETIME; pointer-sized slot is enough for what we use.
        public IntPtr Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InternetPerConnOptionList
    {
        public int Size;
        public IntPtr Connection;
        public int OptionCount;
        public int OptionError;
        public IntPtr Options;
    }

    private static class NativeMethods
    {
        [DllImport("wininet.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool InternetSetOptionW(IntPtr hInternet, int option, ref InternetPerConnOptionList buffer, int bufferLength);

        [DllImport("wininet.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool InternetSetOptionW(IntPtr hInternet, int option, IntPtr buffer, int bufferLength);

        [DllImport("wininet.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool InternetQueryOptionW(IntPtr hInternet, int option, ref InternetPerConnOptionList buffer, ref int bufferLength);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GlobalFree(IntPtr hMem);
    }
}
