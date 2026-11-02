using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PsiSplit.App;

/// <summary>
/// Enables the dark DWM title bar. Windows 10 and 11 use different attribute
/// numbers for this feature, so both are attempted.
/// </summary>
internal static class WindowTheme
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int value,
        int valueSize);

    public static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var dark = 1;
        if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeLegacy, ref dark, sizeof(int));
        }

        if (Environment.OSVersion.Version.Build >= 22000)
        {
            var caption = ToColorRef(0x0B, 0x0E, 0x14);
            var text = ToColorRef(0xE7, 0xEA, 0xF0);
            DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref caption, sizeof(int));
            DwmSetWindowAttribute(handle, DwmwaTextColor, ref text, sizeof(int));
        }
    }

    private static int ToColorRef(byte red, byte green, byte blue)
        => red | (green << 8) | (blue << 16);
}
