using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameValueEditor.Models;

namespace GameValueEditor.Services;

public sealed class ProfileStore
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string DefaultRoot => Path.Combine(AppContext.BaseDirectory, "data");

    public ProfileStore(string? root = null)
    {
        root ??= DefaultRoot;
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
        if (!File.Exists(LibraryPath)) return new LibraryDocument();

        try
        {
            await using var stream = File.OpenRead(LibraryPath);
            return Upgrade(await JsonSerializer.DeserializeAsync<LibraryDocument>(stream, _jsonOptions)
                           ?? new LibraryDocument());
        }
        catch when (File.Exists(BackupPath))
        {
            await using var stream = File.OpenRead(BackupPath);
            return Upgrade(await JsonSerializer.DeserializeAsync<LibraryDocument>(stream, _jsonOptions)
                           ?? new LibraryDocument());
        }
    }

    public async Task SaveAsync(LibraryDocument document)
    {
        var tempPath = LibraryPath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, document, _jsonOptions);
        }

        if (File.Exists(LibraryPath)) File.Copy(LibraryPath, BackupPath, true);
        File.Move(tempPath, LibraryPath, true);
    }

    private static LibraryDocument Upgrade(LibraryDocument document)
    {
        document.SchemaVersion = Math.Max(document.SchemaVersion, 6);
        foreach (var field in document.Games.SelectMany(game => game.Versions).SelectMany(version => version.Fields))
        {
            if (string.IsNullOrWhiteSpace(field.Group)) field.Group = "未分组";
            if (!field.IsValueLocked) field.LockedValue = string.Empty;
        }
        return document;
    }
}
