using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameValueEditor.Models;

namespace GameValueEditor.Services;

public sealed class ProcessService
{
    public IReadOnlyList<ProcessItem> GetProcesses()
    {
        var currentId = Environment.ProcessId;
        var result = new List<ProcessItem>();

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Id == currentId || process.HasExited) continue;
                var path = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(path)) continue;

                result.Add(new ProcessItem
                {
                    ProcessId = process.Id,
                    ProcessName = process.ProcessName,
                    WindowTitle = process.MainWindowTitle,
                    ExecutablePath = path,
                    StartTimeUtc = process.StartTime.ToUniversalTime(),
                    Icon = TryLoadIcon(path)
                });
            }
            catch
            {
                // Protected system processes are intentionally omitted.
            }
            finally
            {
                process.Dispose();
            }
        }

        return result
            .OrderByDescending(item => !string.IsNullOrWhiteSpace(item.WindowTitle))
            .ThenBy(item => item.ProcessName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public ProcessItem? FindRunningGame(GameProfile game) => GetProcesses().FirstOrDefault(item =>
        string.Equals(item.ExecutablePath, game.ExecutablePath, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(item.ProcessName, game.ProcessName, StringComparison.OrdinalIgnoreCase));

    private static ImageSource? TryLoadIcon(string executablePath)
    {
        var info = new ShellFileInfo();
        var result = SHGetFileInfo(executablePath, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(),
            ShellIcon | ShellSmallIcon);
        if (result == 0 || info.IconHandle == IntPtr.Zero) return null;

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                info.IconHandle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(20, 20));
            source.Freeze();
            return source;
        }
        finally
        {
            DestroyIcon(info.IconHandle);
        }
    }

    private const uint ShellIcon = 0x000000100;
    private const uint ShellSmallIcon = 0x000000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr IconHandle;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string path,
        uint fileAttributes,
        ref ShellFileInfo fileInfo,
        uint fileInfoSize,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr iconHandle);
}
