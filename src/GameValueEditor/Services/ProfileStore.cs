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

    public ProfileStore()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GameValueEditor");
        Directory.CreateDirectory(root);
        LibraryPath = Path.Combine(root, "library.json");
        BackupPath = Path.Combine(root, "library.backup.json");
    }

    public string LibraryPath { get; }
    public string BackupPath { get; }

    public async Task<LibraryDocument> LoadAsync()
    {
        if (!File.Exists(LibraryPath)) return new LibraryDocument();

        try
        {
            await using var stream = File.OpenRead(LibraryPath);
            return await JsonSerializer.DeserializeAsync<LibraryDocument>(stream, _jsonOptions)
                   ?? new LibraryDocument();
        }
        catch when (File.Exists(BackupPath))
        {
            await using var stream = File.OpenRead(BackupPath);
            return await JsonSerializer.DeserializeAsync<LibraryDocument>(stream, _jsonOptions)
                   ?? new LibraryDocument();
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
}
