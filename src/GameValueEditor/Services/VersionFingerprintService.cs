using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using GameValueEditor.Models;

namespace GameValueEditor.Services;

public sealed class VersionFingerprintService
{
    public async Task<VersionFingerprint> CreateAsync(string executablePath, CancellationToken cancellationToken = default)
    {
        var fileInfo = new FileInfo(executablePath);
        var versionInfo = FileVersionInfo.GetVersionInfo(executablePath);
        var hash = await ComputeSha256Async(executablePath, cancellationToken);
        var fileVersion = CleanVersion(versionInfo.FileVersion);
        var productVersion = CleanVersion(versionInfo.ProductVersion);
        var display = !string.IsNullOrWhiteSpace(productVersion)
            ? productVersion
            : !string.IsNullOrWhiteSpace(fileVersion)
                ? fileVersion
                : fileInfo.LastWriteTime.ToString("yyyy.MM.dd.HHmm");

        return new VersionFingerprint(
            display,
            fileVersion,
            productVersion,
            hash,
            fileInfo.Length,
            ReadArchitecture(executablePath));
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
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
