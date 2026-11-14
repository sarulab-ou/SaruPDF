using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SaruPdf.WindowsIntegration;

internal static class ExplorerLocation
{
    internal static void Open(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path))
        {
            string folder = Path.GetDirectoryName(path)!;
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("PDFの保存先フォルダーが見つかりません。");
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            return;
        }
        Application.OleRequired();
        nint item = 0;
        try
        {
            Marshal.ThrowExceptionForHR(SHParseDisplayName(path, 0, out item, 0, out _));
            Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(item, 0, 0, 0));
        }
        finally { Marshal.FreeCoTaskMem(item); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, nint context, out nint item, uint attributes, out uint resultAttributes);
    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(nint folder, uint count, nint items, uint flags);
}
