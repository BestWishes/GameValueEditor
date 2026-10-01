using System.Collections.ObjectModel;

namespace GameValueEditor.Models;

public sealed class LibraryDocument
{
    public int SchemaVersion { get; set; } = 7;
    public string Theme { get; set; } = "Light";
    public ObservableCollection<GameProfile> Games { get; set; } = [];
}

public sealed record VersionFingerprint(
    string DisplayName,
    string FileVersion,
    string ProductVersion,
    string Sha256,
    long FileSize,
    string Architecture,
    string BuildSha256,
    string GameAssemblySha256,
    string MetadataSha256,
    string PlatformName = "",
    string PlatformAppId = "",
    string PlatformBuildId = "",
    string PlatformDisplayName = "");
