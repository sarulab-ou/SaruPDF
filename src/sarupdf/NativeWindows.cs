using System.Runtime.InteropServices;

namespace SaruPdf;

internal static class NativeWindows
{
    internal static readonly Color PageOperationTitleColor = Color.FromArgb(32, 88, 150);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref uint value, int size);

    internal static void SetPageOperationTitleBar(Form form, bool enabled)
        => SetTitleBarColor(form, enabled ? PageOperationTitleColor : null);

    internal static void SetTitleBarColor(Form form, Color? color)
    {
        // Caption/text colors are supported by DWM starting with Windows 11.
        if (!form.IsHandleCreated || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        uint caption = color is { } value ? (uint)ColorTranslator.ToWin32(value) : 0xFFFFFFFF;
        uint text = color is not null ? 0x00FFFFFF : 0xFFFFFFFF;
        _ = DwmSetWindowAttribute(form.Handle, 35, ref caption, sizeof(uint)); // DWMWA_CAPTION_COLOR
        _ = DwmSetWindowAttribute(form.Handle, 36, ref text, sizeof(uint)); // DWMWA_TEXT_COLOR
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FlashInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public nint Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    internal static void Activate(Form form)
    {
        if (form.IsDisposed) return;
        if (!form.Visible) form.Show();
        if (IsIconic(form.Handle)) ShowWindow(form.Handle, 9); // SW_RESTORE retains maximized restore state.
        if (!SetForegroundWindow(form.Handle))
        {
            var info = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = form.Handle, Flags = 3, Count = 3 };
            FlashWindowEx(ref info);
        }
    }
}
