using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using GameValueEditor.Models;

namespace GameValueEditor.Services;

public sealed class VersionFingerprintService
{
    private readonly GameVersionMetadataService _metadataService;

    public VersionFingerprintService(GameVersionMetadataService? metadataService = null)
    {
        _metadataService = metadataService ?? new GameVersionMetadataService();
    }

    public async Task<VersionFingerprint> CreateAsync(string executablePath, CancellationToken cancellationToken = default)
    {
        var fileInfo = new FileInfo(executablePath);
        var versionInfo = FileVersionInfo.GetVersionInfo(executablePath);
        var hash = await ComputeSha256Async(executablePath, cancellationToken);
        var root = Path.GetDirectoryName(executablePath) ?? string.Empty;
        var executableName = Path.GetFileNameWithoutExtension(executablePath);
        var gameAssemblyPath = Path.Combine(root, "GameAssembly.dll");
        var metadataPath = Path.Combine(root, $"{executableName}_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        var gameAssemblyHash = File.Exists(gameAssemblyPath)
            ? await ComputeSha256Async(gameAssemblyPath, cancellationToken)
            : string.Empty;
        var metadataHash = File.Exists(metadataPath)
            ? await ComputeSha256Async(metadataPath, cancellationToken)
            : string.Empty;
        var packagePath = GameIdentityEvidenceService.PackagePath(executablePath);
        var packageHash = packagePath.Length > 0 ? await ComputeSha256Async(packagePath, cancellationToken) : string.Empty;
        var buildHash = CreateBuildFingerprint(hash, gameAssemblyHash, metadataHash, packageHash);
        var fileVersion = CleanVersion(versionInfo.FileVersion);
        var productVersion = CleanVersion(versionInfo.ProductVersion);
        var display = !string.IsNullOrWhiteSpace(productVersion)
            ? productVersion
            : !string.IsNullOrWhiteSpace(fileVersion)
                ? fileVersion
                : fileInfo.LastWriteTime.ToString("yyyy.MM.dd.HHmm");

        var platform = _metadataService.ReadPlatformMetadata(executablePath);
        return new VersionFingerprint(
            display,
            fileVersion,
            productVersion,
            hash,
            fileInfo.Length,
            ReadArchitecture(executablePath),
            buildHash,
            gameAssemblyHash,
            metadataHash,
            platform.PlatformName,
            platform.AppId,
            platform.BuildId,
            platform.DisplayName,
            packageHash,
            new GameIdentityEvidenceService().Read(executablePath));
    }

    internal static string CreateBuildFingerprint(string executableHash, string gameAssemblyHash, string metadataHash, string packageHash = "")
    {
        if (string.IsNullOrWhiteSpace(gameAssemblyHash) && string.IsNullOrWhiteSpace(metadataHash) && string.IsNullOrWhiteSpace(packageHash))
            return executableHash;
        var text = $"exe:{executableHash}\nassembly:{gameAssemblyHash}\nmetadata:{metadataHash}";
        if (!string.IsNullOrWhiteSpace(packageHash)) text += $"\npackage:{packageHash}";
        var components = Encoding.UTF8.GetBytes(text);
        return Convert.ToHexString(SHA256.HashData(components));
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        var before = new FileInfo(path);
        var length = before.Length;
        var modified = before.LastWriteTimeUtc;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        before.Refresh();
        if (before.Length != length || before.LastWriteTimeUtc != modified)
            throw new IOException("游戏文件在构建核验期间发生变化，请等待更新完成后重新连接。");
        return Convert.ToHexString(hash);
    }

    private static string CleanVersion(string? value) => value?.Trim().TrimEnd('.') ?? string.Empty;

    private static string ReadArchitecture(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new BinaryReader(stream);
            stream.Position = 0x3C;
            var peOffset = reader.ReadInt32();
            stream.Position = peOffset;
            if (reader.ReadUInt32() != 0x00004550) return "Unknown";
            return reader.ReadUInt16() switch
            {
                0x014C => "x86",
                0x8664 => "x64",
                0xAA64 => "ARM64",
                _ => "Unknown"
            };
        }
        catch
        {
            return "Unknown";
        }
    }
}
