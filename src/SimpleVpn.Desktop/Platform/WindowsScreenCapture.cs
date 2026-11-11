using System.Runtime.InteropServices;
using SimpleVpn.App.Services;
using SimpleVpn.Core.Qr;

namespace SimpleVpn.Desktop.Platform;

/// <summary>Captures the whole virtual screen (all monitors) with GDI as top-down BGRA.</summary>
internal sealed class WindowsScreenCapture : IScreenCapture
{
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const int SrcCopy = 0x00CC0020;
    private const int CaptureBlt = 0x40000000;

    public RawImage? Capture()
    {
        int x = NativeMethods.GetSystemMetrics(SmXVirtualScreen), y = NativeMethods.GetSystemMetrics(SmYVirtualScreen);
        int width = NativeMethods.GetSystemMetrics(SmCxVirtualScreen), height = NativeMethods.GetSystemMetrics(SmCyVirtualScreen);
        if (width <= 0 || height <= 0 || (long)width * height > RawImage.MaxPixels)
        {
            return null;
        }

        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return null;
        }

        var memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
        var bitmap = NativeMethods.CreateCompatibleBitmap(screenDc, width, height);
        try
        {
            var old = NativeMethods.SelectObject(memoryDc, bitmap);
            var copied = NativeMethods.BitBlt(memoryDc, 0, 0, width, height, screenDc, x, y, SrcCopy | CaptureBlt);
            NativeMethods.SelectObject(memoryDc, old);
            if (!copied)
            {
                return null;
            }

            var header = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // top-down rows
                biPlanes = 1,
                biBitCount = 32,
            };
            var pixels = new byte[width * height * 4];
            var lines = NativeMethods.GetDIBits(memoryDc, bitmap, 0, (uint)height, pixels, ref header, 0);
            return lines == height ? new RawImage(pixels, width, height) : null;
        }
        finally
        {
            NativeMethods.DeleteObject(bitmap);
            NativeMethods.DeleteDC(memoryDc);
            _ = NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr src, int srcX, int srcY, int rop);

        [DllImport("gdi32.dll")]
        public static extern int GetDIBits(IntPtr hdc, IntPtr bitmap, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFOHEADER info, uint usage);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteDC(IntPtr hdc);
    }
}
