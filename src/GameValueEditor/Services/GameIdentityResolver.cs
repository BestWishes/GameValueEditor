using System.IO;
using System.Text;
using GameValueEditor.Models;
using GameValueEditor.Services.Adapters;

namespace GameValueEditor.Services;

internal static class GameIdentityResolver
{
    internal static GameProfile? Resolve(IEnumerable<GameProfile> games, ProcessItem process,
        VersionFingerprint? fingerprint = null, IReadOnlyCollection<string>? confirmedModuleIds = null,
        Func<string, IReadOnlyList<string>>? moduleNames = null, GameProfile? preferredGame = null,
        IReadOnlyCollection<string>? runningNames = null)
    {
        // Game identity is a name, not a build or an adapter's Supports result.
        // Build hashes remain useful below ONLY for isolating version-bound scan addresses.
        var names = new[] { process.WindowTitle, process.ProcessName, fingerprint?.Identity?.ProductName ?? "" }
            .Concat(runningNames ?? [])
            .Select(NormalizeName).Where(IsGameName).ToHashSet(StringComparer.Ordinal);
        var candidates = games.Where(game => Names(game, moduleNames).Any(names.Contains)).ToArray();
        if (preferredGame is not null && candidates.Contains(preferredGame)) return preferredGame;
        return Unique(candidates);
    }

    private static IEnumerable<string> Names(GameProfile game, Func<string, IReadOnlyList<string>>? moduleNames) =>
        new[] { game.Name, game.ProcessName, game.Identity?.ProductName ?? "" }
            .Concat(moduleNames?.Invoke(game.ModuleId) ?? []).Select(NormalizeName).Where(IsGameName);

    internal static string NormalizeName(string name)
    {
        name = name.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return string.Concat(name.Normalize(NormalizationForm.FormKC).Where(char.IsLetterOrDigit)).ToUpperInvariant();
    }

    // These are runtime executable names, not names of games. An actual window title,
    // product name or declared alias still identifies a game using these runtimes.
    private static bool IsGameName(string name) => name.Length > 0 &&
        name is not ("GAME" or "ELECTRON" or "NW" or "NWJS" or "UNITYPLAYER");

    internal static bool MatchesVersion(GameVersionProfile version, VersionFingerprint fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint.BuildSha256)) return false;
        if (!string.IsNullOrWhiteSpace(version.BuildFingerprint))
            return Equal(version.BuildFingerprint, fingerprint.BuildSha256);
        // A legacy EXE-only record cannot identify an IL2CPP build. Complete saved
        // component hashes, however, provide exactly the same identity as the combination.
        return !string.IsNullOrWhiteSpace(version.ExecutableSha256) &&
               Equal(version.ExecutableSha256, fingerprint.Sha256) &&
               Equal(version.GameAssemblySha256, fingerprint.GameAssemblySha256) &&
               Equal(version.MetadataSha256, fingerprint.MetadataSha256) && Equal(version.PackageSha256, fingerprint.PackageSha256);
    }

    internal static bool MatchesInstalledBuild(GameModuleBuildMatch build, GameVersionProfile version)
    {
        if (new[] { build.BuildFingerprint, build.ExecutableSha256, build.GameAssemblySha256, build.MetadataSha256, build.PackageSha256 }
            .All(string.IsNullOrWhiteSpace)) return false;
        var fingerprint = version.BuildFingerprint;
        if (string.IsNullOrWhiteSpace(fingerprint) && !string.IsNullOrWhiteSpace(version.ExecutableSha256))
            fingerprint = VersionFingerprintService.CreateBuildFingerprint(version.ExecutableSha256,
                version.GameAssemblySha256, version.MetadataSha256, version.PackageSha256);
        return Optional(build.BuildFingerprint, fingerprint) && Optional(build.ExecutableSha256, version.ExecutableSha256) &&
               Optional(build.GameAssemblySha256, version.GameAssemblySha256) && Optional(build.MetadataSha256, version.MetadataSha256) &&
               Optional(build.PackageSha256, version.PackageSha256);
    }

    internal static bool PathsEqual(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try { return Equal(Path.GetFullPath(left), Path.GetFullPath(right)); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { return false; }
    }

    internal static GameIdentityEvidence Merge(GameIdentityEvidence? saved, GameIdentityEvidence current) => current with
    {
        InstallationExecutablePath = Keep(current.InstallationExecutablePath, saved?.InstallationExecutablePath),
        ExecutableName = Keep(current.ExecutableName, saved?.ExecutableName),
        ProductName = Keep(current.ProductName, saved?.ProductName), PackageId = Keep(current.PackageId, saved?.PackageId),
        PlatformName = current.PlatformName.Length > 0 && current.PlatformAppId.Length > 0 ? current.PlatformName : saved?.PlatformName ?? "",
        PlatformAppId = current.PlatformName.Length > 0 && current.PlatformAppId.Length > 0 ? current.PlatformAppId : saved?.PlatformAppId ?? ""
    };

    private static string Keep(string current, string? saved) => string.IsNullOrWhiteSpace(current) ? saved ?? "" : current;

    private static GameProfile? Unique(GameProfile[] games) => games.Length == 1 ? games[0] : null;
    private static bool Equal(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static bool Optional(string expected, string actual) => string.IsNullOrWhiteSpace(expected) || Equal(expected, actual);
}
