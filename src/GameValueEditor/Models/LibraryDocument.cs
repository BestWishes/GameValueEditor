using System.Collections.ObjectModel;

namespace GameValueEditor.Models;

public sealed class LibraryDocument
{
    public int SchemaVersion { get; set; } = 1;
    public ObservableCollection<GameProfile> Games { get; set; } = [];
}

public sealed record VersionFingerprint(
    string DisplayName,
    string FileVersion,
    string ProductVersion,
    string Sha256,
    long FileSize,
    string Architecture);
