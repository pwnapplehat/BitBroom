using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace BitBroom.App.Interop;

/// <summary>Windows 11 DWM window styling (rounded corners, dark chrome). No-ops on Windows 10.</summary>
internal static class Dwm
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void ApplyWindowStyling(Window window)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            int darkMode = 1;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

            int corners = DWMWCP_ROUND;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));
        }
        catch (Exception)
        {
            // Purely cosmetic; older Windows builds simply ignore these attributes.
        }
    }
}
