using System.Runtime.InteropServices;

namespace SaruPdf;

internal static class NativeWindows
{
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
