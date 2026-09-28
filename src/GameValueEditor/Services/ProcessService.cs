using System.Diagnostics;
using System.IO;
using System.Management;
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
        var metadata = ReadProcessMetadata();
        var result = new List<ProcessItem>();

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Id == currentId || process.HasExited) continue;
                metadata.TryGetValue(process.Id, out var details);
                var path = details?.ExecutablePath;
                if (string.IsNullOrWhiteSpace(path)) path = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(path)) continue;

                var commandLine = details?.CommandLine ?? string.Empty;
                var runtime = DetectRuntime(path);
                result.Add(new ProcessItem
                {
                    ProcessId = process.Id,
                    ParentProcessId = details?.ParentProcessId ?? 0,
                    ProcessName = process.ProcessName,
                    WindowTitle = process.MainWindowTitle,
                    ExecutablePath = path,
                    CommandLine = commandLine,
                    StartTimeUtc = process.StartTime.ToUniversalTime(),
                    WorkingSetBytes = process.WorkingSet64,
                    RuntimeKind = runtime,
                    Role = ClassifyRole(runtime, commandLine),
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
            .ThenBy(item => item.ProcessId)
            .ToList();
    }

    public ProcessItem? FindRunningGame(GameProfile game)
    {
        var candidates = GetProcesses().Where(item =>
            PathsEqual(item.ExecutablePath, game.ExecutablePath) ||
            string.Equals(item.ProcessName, game.ProcessName, StringComparison.OrdinalIgnoreCase));
        return candidates
            .OrderByDescending(item => item.Role == GameProcessRole.Main)
            .ThenByDescending(item => !string.IsNullOrWhiteSpace(item.WindowTitle))
            .ThenByDescending(item => item.WorkingSetBytes)
            .FirstOrDefault();
    }

    public LogicalGameProcessGroup ResolveLogicalGame(
        ProcessItem seed,
        IReadOnlyCollection<ProcessItem>? processSnapshot = null)
    {
        var processes = processSnapshot ?? GetProcesses();
        var canonicalSeed = processes.FirstOrDefault(item =>
                                item.ProcessId == seed.ProcessId && item.StartTimeUtc == seed.StartTimeUtc)
                            ?? seed;
        var byId = processes
            .GroupBy(item => item.ProcessId)
            .ToDictionary(group => group.Key, group => group.First());

        var root = canonicalSeed;
        var visited = new HashSet<int> { root.ProcessId };
        while (root.ParentProcessId > 0 &&
               byId.TryGetValue(root.ParentProcessId, out var parent) &&
               visited.Add(parent.ProcessId) &&
               PathsEqual(parent.ExecutablePath, canonicalSeed.ExecutablePath) &&
               parent.StartTimeUtc <= root.StartTimeUtc)
        {
            root = parent;
        }

        var childrenByParent = processes
            .Where(item => PathsEqual(item.ExecutablePath, canonicalSeed.ExecutablePath))
            .GroupBy(item => item.ParentProcessId)
            .ToDictionary(group => group.Key, group => group.ToList());
        var members = new List<ProcessItem>();
        var queue = new Queue<ProcessItem>();
        var memberIds = new HashSet<int>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!memberIds.Add(current.ProcessId)) continue;
            members.Add(current);
            if (!childrenByParent.TryGetValue(current.ProcessId, out var children)) continue;
            foreach (var child in children.Where(child => child.StartTimeUtc >= root.StartTimeUtc))
                queue.Enqueue(child);
        }

        if (!memberIds.Contains(canonicalSeed.ProcessId)) members.Add(canonicalSeed);
        var webRuntime = members.Select(item => item.RuntimeKind)
            .FirstOrDefault(kind => kind is GameRuntimeKind.Electron or GameRuntimeKind.NwJs);
        var runtime = webRuntime is GameRuntimeKind.Electron or GameRuntimeKind.NwJs
            ? webRuntime
            : canonicalSeed.RuntimeKind;
        var dataProcess = SelectDataProcess(runtime, root, canonicalSeed, members);

        return new LogicalGameProcessGroup
        {
            SeedProcess = canonicalSeed,
            RootProcess = root,
            DataProcess = dataProcess,
            Members = members.OrderBy(item => item.StartTimeUtc).ThenBy(item => item.ProcessId).ToList(),
            RuntimeKind = runtime
        };
    }

    private static ProcessItem SelectDataProcess(
        GameRuntimeKind runtime,
        ProcessItem root,
        ProcessItem seed,
        IReadOnlyCollection<ProcessItem> members)
    {
        if (runtime is GameRuntimeKind.Electron or GameRuntimeKind.NwJs)
        {
            var renderer = members
                .Where(item => item.Role == GameProcessRole.Renderer)
                .OrderByDescending(item => item.WorkingSetBytes)
                .FirstOrDefault();
            if (renderer is not null) return renderer;
        }

        // Some native/Unity launchers start a second copy of the same executable and
        // keep the small outer process alive. All non-web processes are classified as
        // Main, so the role alone cannot identify the process that owns the game state.
        // Prefer the member that owns a top-level window, using working set only to
        // distinguish multiple windowed members. This also makes selecting either the
        // outer launcher or the inner game process resolve to the same data process.
        var windowedGameProcess = members
            .Where(item => !string.IsNullOrWhiteSpace(item.WindowTitle))
            .OrderByDescending(item => item.WorkingSetBytes)
            .ThenBy(item => item.StartTimeUtc)
            .ThenBy(item => item.ProcessId)
            .FirstOrDefault();
        if (windowedGameProcess is not null) return windowedGameProcess;

        // Headless and early-startup games have no usable window signal. Keep the
        // established root fallback instead of guessing from memory size alone.
        if (root.Role == GameProcessRole.Main) return root;
        return members.OrderByDescending(item => item.WorkingSetBytes).FirstOrDefault() ?? seed;
    }

    private static Dictionary<int, ProcessMetadata> ReadProcessMetadata()
    {
        var result = new Dictionary<int, ProcessMetadata>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId, CommandLine, ExecutablePath FROM Win32_Process");
            using var objects = searcher.Get();
            foreach (ManagementObject item in objects)
            {
                using (item)
                {
                    var processId = Convert.ToInt32(item["ProcessId"] ?? 0);
                    if (processId <= 0) continue;
                    result[processId] = new ProcessMetadata(
                        Convert.ToInt32(item["ParentProcessId"] ?? 0),
                        item["CommandLine"] as string ?? string.Empty,
                        item["ExecutablePath"] as string ?? string.Empty);
                }
            }
        }
        catch
        {
            // WMI can be unavailable. The process list remains usable as a
            // single-process fallback when parent/command-line data is missing.
        }
        return result;
    }

    private static GameRuntimeKind DetectRuntime(string executablePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(executablePath);
            if (string.IsNullOrWhiteSpace(directory)) return GameRuntimeKind.Unknown;
            if (File.Exists(Path.Combine(directory, "resources", "app.asar"))) return GameRuntimeKind.Electron;
            if (File.Exists(Path.Combine(directory, "nw.dll")) || File.Exists(Path.Combine(directory, "package.nw")))
                return GameRuntimeKind.NwJs;
            if (File.Exists(Path.Combine(directory, "GameAssembly.dll"))) return GameRuntimeKind.UnityIl2Cpp;
            if (File.Exists(Path.Combine(directory, "UnityPlayer.dll")))
            {
                var name = Path.GetFileNameWithoutExtension(executablePath);
                var dataDirectory = Path.Combine(directory, $"{name}_Data");
                return Directory.Exists(Path.Combine(dataDirectory, "MonoBleedingEdge"))
                    ? GameRuntimeKind.UnityMono
                    : GameRuntimeKind.Unity;
            }
            return GameRuntimeKind.Native;
        }
        catch
        {
            return GameRuntimeKind.Unknown;
        }
    }

    private static GameProcessRole ClassifyRole(GameRuntimeKind runtime, string commandLine)
    {
        if (runtime is not (GameRuntimeKind.Electron or GameRuntimeKind.NwJs)) return GameProcessRole.Main;
        if (string.IsNullOrWhiteSpace(commandLine) ||
            !commandLine.Contains("--type=", StringComparison.OrdinalIgnoreCase)) return GameProcessRole.Main;
        if (commandLine.Contains("--type=renderer", StringComparison.OrdinalIgnoreCase)) return GameProcessRole.Renderer;
        if (commandLine.Contains("--type=gpu-process", StringComparison.OrdinalIgnoreCase)) return GameProcessRole.Gpu;
        if (commandLine.Contains("audio.mojom.AudioService", StringComparison.OrdinalIgnoreCase)) return GameProcessRole.Audio;
        if (commandLine.Contains("network.mojom.NetworkService", StringComparison.OrdinalIgnoreCase)) return GameProcessRole.Network;
        if (commandLine.Contains("--type=utility", StringComparison.OrdinalIgnoreCase)) return GameProcessRole.Utility;
        return GameProcessRole.Unknown;
    }

    private static bool PathsEqual(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(left, right, StringComparison.OrdinalIgnoreCase); }
    }

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

    private sealed record ProcessMetadata(int ParentProcessId, string CommandLine, string ExecutablePath);

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
