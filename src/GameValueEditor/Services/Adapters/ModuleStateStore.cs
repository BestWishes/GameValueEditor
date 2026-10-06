using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameValueEditor.Services.Adapters;

// Call mutations only while holding ModuleMutationLock. Backups are recovery evidence,
// never an instruction to silently select older modules or replay old deletion tasks.
internal sealed class ModuleStateStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    internal string Root { get; }
    internal ModuleStateStore(string root) => Root = Path.GetFullPath(root);
    internal string InstalledPath => Resolve("installed.json");
    internal string DeletionsPath => Resolve("pending-deletions.json");

    internal InstalledModuleDocument ReadInstalled() => Read<InstalledModuleDocument>(InstalledPath, "Modules", ValidateInstalled, () => new());
    internal PendingModuleDeletionDocument ReadDeletions() => Read<PendingModuleDeletionDocument>(DeletionsPath, "ModuleIds", ValidateDeletions, () => new());
    internal void SaveInstalled(InstalledModuleDocument document) => Save(InstalledPath, "Modules", document, ValidateInstalled, () => new());
    internal void SaveDeletions(PendingModuleDeletionDocument document) => Save(DeletionsPath, "ModuleIds", document, ValidateDeletions, () => new());

    private T Read<T>(string path, string collection, Action<T> validate, Func<T> empty)
    {
        EnsureNoLinks(path);
        if (!FileExists(path))
        {
            if (FileExists(path + ".backup"))
                throw new InvalidDataException($"{Path.GetFileName(path)} 主记录缺失，但备份仍在；请手动恢复资料后重启，不能按空记录继续。");
            return empty();
        }
        try
        {
            if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("记录过大。");
            using var json = JsonDocument.Parse(File.ReadAllBytes(path));
            var properties = json.RootElement.ValueKind == JsonValueKind.Object ? json.RootElement.EnumerateObject().ToArray() : [];
            if (properties.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != properties.Length ||
                !properties.Any(item => item.Name.Equals("SchemaVersion", StringComparison.OrdinalIgnoreCase)) ||
                !properties.Any(item => item.Name.Equals(collection, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("记录缺少必需字段或含重复字段。");
            var document = json.Deserialize<T>(JsonOptions) ?? throw new InvalidDataException("记录为空。");
            validate(document);
            return document;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} 无法安全读取；原记录与备份已保留，请手动恢复资料后重启。", exception);
        }
    }

    private void Save<T>(string path, string collection, T document, Action<T> validate, Func<T> empty)
    {
        validate(document);
        _ = Read(path, collection, validate, empty); // Never overwrite an unreadable source.
        EnsureNoLinks(path + ".backup");
        Directory.CreateDirectory(Root);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            WriteDurable(temporary, bytes);
            if (FileExists(path)) File.Replace(temporary, path, path + ".backup");
            else
            {
                File.Move(temporary, path);
                // Publish the durable primary first: a crash before the initial
                // backup must not leave a backup-only ambiguous registration.
                WriteAtomic(path + ".backup", bytes);
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static void ValidateInstalled(InstalledModuleDocument document)
    {
        if (document.SchemaVersion != 1) throw new NotSupportedException("installed.json Schema 不受支持，已拒绝读取或覆盖。");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (document.Modules is null || document.Modules.Any(item => item is null || !IsSegment(item.Id) ||
            !IsVersion(item.Version) || !seen.Add(item.Id)))
            throw new InvalidDataException("installed.json 包含无效或重复的模块记录。");
    }

    private static void ValidateDeletions(PendingModuleDeletionDocument document)
    {
        if (document.SchemaVersion != 1) throw new NotSupportedException("pending-deletions.json Schema 不受支持，已拒绝读取或覆盖。");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (document.ModuleIds is null || document.ModuleIds.Any(id => !IsSegment(id) || !seen.Add(id)))
            throw new InvalidDataException("pending-deletions.json 包含无效或重复的模块身份。");
    }

    internal static bool IsSegment(string? value) => !string.IsNullOrWhiteSpace(value) && value is not "." and not ".." &&
        !value.EndsWith('.') && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');
    internal static bool IsVersion(string? value) => Regex.IsMatch(value ?? "", @"^(0|[1-9][0-9]*)\.[0-9]\.[0-9]$") && Version.TryParse(value, out _);

    internal string Resolve(string relative)
    {
        if (Path.IsPathRooted(relative)) throw new InvalidDataException("模块资料路径必须位于模块目录内。");
        var result = Path.GetFullPath(Path.Combine(Root, relative));
        if (!result.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("模块资料路径越出模块目录。");
        EnsureNoLinks(result);
        return result;
    }

    internal void EnsureNoLinks(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if (Attributes(current) is { } attributes && (attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("模块资料路径含符号链接或目录联接，已拒绝变更。");
            if (string.Equals(current, Root, StringComparison.OrdinalIgnoreCase)) break;
        }
    }

    private static FileAttributes? Attributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    private static bool FileExists(string path)
    {
        var attributes = Attributes(path);
        if (attributes is null) return false;
        if ((attributes & FileAttributes.Directory) != 0) throw new InvalidDataException("模块记录路径被目录占用，已保留现场。");
        return true;
    }

    internal Dictionary<string, string> HashPackage(string directory)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var directories = new Stack<string>();
        directories.Push(directory);
        while (directories.TryPop(out var current))
        {
            EnsureNoLinks(current);
            foreach (var file in Directory.EnumerateFiles(current))
            {
                EnsureNoLinks(file);
                if (result.Count >= 4096) throw new InvalidDataException("模块包文件数量超出安全上限。");
                using var stream = File.OpenRead(file);
                result.Add(Path.GetRelativePath(directory, file), Convert.ToHexString(SHA256.HashData(stream)));
            }
            foreach (var child in Directory.EnumerateDirectories(current)) { EnsureNoLinks(child); directories.Push(child); }
        }
        return result;
    }

    internal static void WriteDurable(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16384, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(true);
    }

    internal static void WriteAtomic(string path, byte[] bytes)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { WriteDurable(temporary, bytes); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
