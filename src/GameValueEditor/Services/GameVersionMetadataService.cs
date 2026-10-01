using System.IO;
using System.Text.RegularExpressions;

namespace GameValueEditor.Services;

public sealed class GameVersionMetadataService
{
    private static readonly Regex QuotedPair = new(
        "^\\s*\"(?<key>[^\"]+)\"\\s*\"(?<value>[^\"]*)\"\\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public PlatformGameVersionInfo ReadPlatformMetadata(string executablePath)
    {
        try { return ReadPlatformMetadataCore(executablePath); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return PlatformGameVersionInfo.Empty;
        }
    }

    private static PlatformGameVersionInfo ReadPlatformMetadataCore(string executablePath)
    {
        var executableDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath));
        if (string.IsNullOrWhiteSpace(executableDirectory)) return PlatformGameVersionInfo.Empty;

        var directory = new DirectoryInfo(executableDirectory);
        while (directory is not null && !directory.Name.Equals("common", StringComparison.OrdinalIgnoreCase))
            directory = directory.Parent;
        var steamApps = directory?.Parent;
        if (directory is null || steamApps is null ||
            !steamApps.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase))
            return PlatformGameVersionInfo.Empty;

        var relativeDirectory = Path.GetRelativePath(directory.FullName, executableDirectory)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        foreach (var manifestPath in Directory.EnumerateFiles(steamApps.FullName, "appmanifest_*.acf", SearchOption.TopDirectoryOnly))
        {
            var values = ParseManifest(manifestPath);
            if (!values.TryGetValue("installdir", out var installDirectory) ||
                !string.Equals(installDirectory, relativeDirectory, StringComparison.OrdinalIgnoreCase))
                continue;
            return new PlatformGameVersionInfo(
                "Steam",
                values.GetValueOrDefault("appid", string.Empty),
                values.GetValueOrDefault("buildid", string.Empty),
                values.GetValueOrDefault("name", string.Empty));
        }
        return PlatformGameVersionInfo.Empty;
    }

    private static Dictionary<string, string> ParseManifest(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(path))
        {
            var match = QuotedPair.Match(line);
            if (match.Success) values[match.Groups["key"].Value] = match.Groups["value"].Value;
        }
        return values;
    }
}

public sealed record PlatformGameVersionInfo(
    string PlatformName,
    string AppId,
    string BuildId,
    string DisplayName)
{
    public static PlatformGameVersionInfo Empty { get; } = new(string.Empty, string.Empty, string.Empty, string.Empty);
}
