using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using GameValueEditor.Models;

namespace GameValueEditor.Services;

internal sealed class GameIdentityEvidenceService
{
    private const int MaximumHeaderBytes = 4 * 1024 * 1024;
    private const int MaximumPackageBytes = 64 * 1024;
    private static readonly HashSet<string> CommonLaunchers = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam", "steamwebhelper", "explorer", "cmd", "powershell", "pwsh", "conhost", "WindowsTerminal",
        "dotnet", "node", "python", "pythonw", "wscript", "cscript", "rundll32", "msiexec",
        "EpicGamesLauncher", "EpicWebHelper", "GalaxyClient", "Battle.net", "EADesktop", "UbisoftConnect",
        "chrome", "msedge", "firefox", "Code", "GameValueEditor"
    };

    internal GameIdentityEvidence Read(string executablePath)
    {
        var product = "";
        try { product = FileVersionInfo.GetVersionInfo(executablePath).ProductName?.Trim() ?? ""; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { }
        if (product.Length > 256 || product.Equals("Electron", StringComparison.OrdinalIgnoreCase) ||
            product.Equals("NW.js", StringComparison.OrdinalIgnoreCase)) product = "";
        var temporary = IsTemporaryPath(executablePath);
        var shared = IsCommonExecutable(executablePath);
        var platform = new GameVersionMetadataService().ReadPlatformMetadata(executablePath);
        return new()
        {
            InstallationExecutablePath = temporary || shared ? "" : executablePath,
            ExecutableName = Path.GetFileName(executablePath), ProductName = product,
            PackageId = ReadPackageId(executablePath), IsTemporaryExecutable = temporary, IsSharedExecutable = shared,
            PlatformName = platform.PlatformName, PlatformAppId = platform.AppId
        };
    }

    internal GameIdentityEvidence Read(LogicalGameProcessGroup group, IReadOnlyCollection<ProcessItem> processes)
    {
        var evidence = Read(group.DataProcess.ExecutablePath);
        if (!evidence.IsTemporaryExecutable) return evidence;
        var source = FindLaunchSource(group.RootProcess, processes);
        if (source is null) return evidence;
        var platform = new GameVersionMetadataService().ReadPlatformMetadata(source.ExecutablePath);
        return evidence with { InstallationExecutablePath = source.ExecutablePath,
            PlatformName = platform.PlatformName, PlatformAppId = platform.AppId };
    }

    internal static ProcessItem? FindLaunchSource(ProcessItem root, IReadOnlyCollection<ProcessItem> processes)
    {
        var byId = processes.GroupBy(item => item.ProcessId).ToDictionary(items => items.Key, items => items.First());
        return FindLaunchSource(root, byId);
    }

    internal static ProcessItem? FindLaunchSource(ProcessItem root, IReadOnlyDictionary<int, ProcessItem> byId)
    {
        var current = root;
        var visited = new HashSet<int> { root.ProcessId };
        for (var depth = 0; depth < 8 && current.ParentProcessId > 0; depth++)
        {
            if (!byId.TryGetValue(current.ParentProcessId, out var parent) || !visited.Add(parent.ProcessId) ||
                parent.StartTimeUtc == default || current.StartTimeUtc == default || parent.StartTimeUtc > current.StartTimeUtc)
                return null;
            if (string.IsNullOrWhiteSpace(parent.ExecutablePath)) return null;
            if (!IsTemporaryPath(parent.ExecutablePath))
                return IsCommonExecutable(parent.ExecutablePath) ? null : parent;
            current = parent;
        }
        return null;
    }

    private static bool IsCommonExecutable(string path) => Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
        CommonLaunchers.Contains(Path.GetFileNameWithoutExtension(path));

    internal static bool IsTemporaryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var full = Path.GetFullPath(path);
            return new[] { Path.GetTempPath(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp") }
                .Any(root => full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    internal static string PackagePath(string executablePath)
    {
        var root = Path.GetDirectoryName(executablePath) ?? "";
        var asar = Path.Combine(root, "resources", "app.asar");
        if (File.Exists(asar)) return asar;
        var nw = Path.Combine(root, "package.nw");
        return File.Exists(nw) ? nw : "";
    }

    private static string ReadPackageId(string executablePath)
    {
        try
        {
            var package = PackagePath(executablePath);
            if (package.EndsWith(".asar", StringComparison.OrdinalIgnoreCase)) return ReadAsarPackageId(package);
            if (package.Length > 0)
            {
                using var archive = ZipFile.OpenRead(package);
                var entries = archive.Entries.Where(entry => entry.FullName == "package.json").ToArray();
                if (entries.Length != 1 || entries[0].Length > MaximumPackageBytes) return "";
                using var stream = entries[0].Open();
                return ParsePackageId(ReadBounded(stream));
            }
            var json = Path.Combine(Path.GetDirectoryName(executablePath) ?? "", "package.json");
            if (!File.Exists(json) || (File.GetAttributes(json) & FileAttributes.ReparsePoint) != 0) return "";
            using var file = File.OpenRead(json);
            return ParsePackageId(ReadBounded(file));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or
            InvalidDataException or ArgumentException or OverflowException) { return ""; }
    }

    private static string ReadAsarPackageId(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 16 || reader.ReadUInt32() != 4) return "";
        var headerSize = reader.ReadUInt32();
        if (headerSize < 8 || headerSize > MaximumHeaderBytes || headerSize > stream.Length - 8) return "";
        var header = reader.ReadBytes((int)headerSize);
        var jsonSize = BitConverter.ToUInt32(header, 4);
        if (jsonSize == 0 || jsonSize > headerSize - 8) return "";
        using var document = JsonDocument.Parse(header.AsMemory(8, (int)jsonSize));
        if (!UniqueProperty(document.RootElement, "files", out var files) ||
            !UniqueProperty(files, "package.json", out var entry) || entry.ValueKind != JsonValueKind.Object ||
            entry.TryGetProperty("link", out _) || entry.TryGetProperty("unpacked", out _) ||
            !UniqueProperty(entry, "size", out var sizeElement) || sizeElement.ValueKind != JsonValueKind.Number || !sizeElement.TryGetInt32(out var size) ||
            size <= 0 || size > MaximumPackageBytes || !UniqueProperty(entry, "offset", out var offsetElement) ||
            offsetElement.ValueKind != JsonValueKind.String || !long.TryParse(offsetElement.GetString(), out var offset) || offset < 0)
            return "";
        var start = 8L + headerSize;
        if (offset > stream.Length - start || size > stream.Length - start - offset) return "";
        stream.Position = start + offset;
        return ParsePackageId(reader.ReadBytes(size));
    }

    private static byte[] ReadBounded(Stream stream)
    {
        using var buffer = new MemoryStream();
        var bytes = new byte[4096];
        int count;
        while ((count = stream.Read(bytes)) > 0)
        {
            if (buffer.Length + count > MaximumPackageBytes) throw new InvalidDataException("Package identity exceeds its bounded size.");
            buffer.Write(bytes, 0, count);
        }
        return buffer.ToArray();
    }

    private static string ParsePackageId(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        if (!UniqueProperty(document.RootElement, "name", out var name) || name.ValueKind != JsonValueKind.String) return "";
        var value = name.GetString()?.Trim() ?? "";
        return value.Length is > 0 and <= 256 && !value.Any(char.IsControl) ? value : "";
    }

    private static bool UniqueProperty(JsonElement element, string key, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object) return false;
        var matches = element.EnumerateObject().Where(property => property.NameEquals(key)).ToArray();
        if (matches.Length != 1) return false;
        value = matches[0].Value;
        return true;
    }
}
