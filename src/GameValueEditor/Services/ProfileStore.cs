using System.IO;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameValueEditor.Models;

namespace GameValueEditor.Services;

public sealed class ProfileStore
{
    private const int SupportedSchema = 7;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string DefaultRoot => Path.Combine(AppContext.BaseDirectory, "data");

    public ProfileStore(string? root = null)
    {
        root = Path.GetFullPath(root ?? DefaultRoot);
        Directory.CreateDirectory(root);
        LibraryPath = Path.Combine(root, "library.json");
        BackupPath = Path.Combine(root, "library.backup.json");
        RootDirectory = root;
        IconsDirectory = Path.Combine(root, "icons");
        ModulesDirectory = Path.Combine(root, "modules");
        UpdatesDirectory = Path.Combine(root, "updates");
    }

    public string RootDirectory { get; }
    public string LibraryPath { get; }
    public string BackupPath { get; }
    public string IconsDirectory { get; }
    public string ModulesDirectory { get; }
    public string UpdatesDirectory { get; }

    public async Task<LibraryDocument> LoadAsync()
    {
        using var gate = await AcquireAsync();
        if (!File.Exists(LibraryPath) && !File.Exists(BackupPath)) return new LibraryDocument();
        try
        {
            return ReadValidated(LibraryPath);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            try { return ReadValidated(BackupPath); }
            catch (Exception backupError) when (backupError is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
            {
                throw new InvalidDataException("游戏库主文件与备份均无法安全读取；原文件已保留，请先恢复资料。", new AggregateException(exception, backupError));
            }
        }
    }

    public Task SaveAsync(LibraryDocument document)
    {
        // Capture on the caller's thread before yielding: UI collections remain mutable.
        var snapshot = JsonSerializer.SerializeToUtf8Bytes(document, _jsonOptions);
        Validate(JsonSerializer.Deserialize<LibraryDocument>(snapshot, _jsonOptions));
        return SaveSnapshotAsync(snapshot);
    }

    private async Task SaveSnapshotAsync(byte[] snapshot)
    {
        using var gate = await AcquireAsync();
        var tempPath = LibraryPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var validPrimary = IsValidExisting(LibraryPath);
            var validBackup = IsValidExisting(BackupPath);
            if (!validPrimary && !validBackup && (File.Exists(LibraryPath) || File.Exists(BackupPath)))
                throw new InvalidDataException("游戏库没有有效主文件或备份，已拒绝覆盖原资料。");
            await WriteDurableAsync(tempPath, snapshot);
            if (validPrimary) File.Replace(tempPath, LibraryPath, BackupPath);
            else
            {
                // Never copy a damaged primary over the valid recovery source.
                File.Move(tempPath, LibraryPath, true);
                if (!validBackup)
                {
                    var backupTemporary = BackupPath + $".{Guid.NewGuid():N}.tmp";
                    try
                    {
                        await WriteDurableAsync(backupTemporary, snapshot);
                        File.Move(backupTemporary, BackupPath, true);
                    }
                    finally { TryDelete(backupTemporary); }
                }
            }
        }
        finally { TryDelete(tempPath); }
    }

    private LibraryDocument ReadValidated(string path) =>
        Upgrade(Validate(JsonSerializer.Deserialize<LibraryDocument>(File.ReadAllBytes(path), _jsonOptions)));

    private bool IsValidExisting(string path)
    {
        if (!File.Exists(path)) return false;
        try { ReadValidated(path); return true; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException) { return false; }
    }

    private static LibraryDocument Validate(LibraryDocument? document)
    {
        if (document is null || document.Games is null || document.Games.Any(game =>
                game is null || game.Versions is null || game.Versions.Any(version => version is null || version.Fields is null ||
                    version.Fields.Any(field => field is null))))
            throw new InvalidDataException("游戏库缺少必要的数据结构。");
        if (document.SchemaVersion > SupportedSchema)
            throw new NotSupportedException($"游戏库格式 {document.SchemaVersion} 高于本程序支持的 {SupportedSchema}，请使用较新主程序，不能覆盖现有资料。");
        return document;
    }

    private async Task<IDisposable> AcquireAsync()
    {
        var semaphore = Gates.GetOrAdd(LibraryPath, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync();
        try
        {
            var clock = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    var lease = new FileStream(LibraryPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    return new StoreLease(semaphore, lease);
                }
                catch (IOException) when (clock.Elapsed < TimeSpan.FromSeconds(5)) { await Task.Delay(50); }
            }
        }
        catch { semaphore.Release(); throw; }
    }

    private sealed class StoreLease(SemaphoreSlim semaphore, FileStream lease) : IDisposable
    {
        public void Dispose() { lease.Dispose(); semaphore.Release(); }
    }

    private static async Task WriteDurableAsync(string path, byte[] snapshot)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(snapshot);
        await stream.FlushAsync();
        stream.Flush(true);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static LibraryDocument Upgrade(LibraryDocument document)
    {
        document.SchemaVersion = Math.Max(document.SchemaVersion, 7);
        foreach (var field in document.Games.SelectMany(game => game.Versions).SelectMany(version => version.Fields))
        {
            if (string.IsNullOrWhiteSpace(field.Group)) field.Group = "未分组";
            if (!field.IsValueLocked) field.LockedValue = string.Empty;
        }
        return document;
    }
}
