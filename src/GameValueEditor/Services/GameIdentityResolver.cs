using System.IO;
using GameValueEditor.Models;
using GameValueEditor.Services.Adapters;

namespace GameValueEditor.Services;

internal static class GameIdentityResolver
{
    internal static GameProfile? Resolve(IEnumerable<GameProfile> games, ProcessItem process,
        VersionFingerprint? fingerprint = null, IReadOnlyCollection<string>? confirmedModuleIds = null)
    {
        var candidates = games.Where(game => confirmedModuleIds is not { Count: > 0 } ||
            string.IsNullOrWhiteSpace(game.ModuleId) || confirmedModuleIds.Contains(game.ModuleId, StringComparer.Ordinal)).ToArray();
        var paths = candidates.Where(game => PathsEqual(game.ExecutablePath, process.ExecutablePath)).ToArray();
        if (paths.Length > 0) return Unique(paths);
        if (confirmedModuleIds is { Count: > 0 })
        {
            var modules = candidates.Where(game => confirmedModuleIds.Contains(game.ModuleId, StringComparer.Ordinal)).ToArray();
            if (modules.Length > 0) return Unique(modules);
        }
        return fingerprint is null ? null : Unique(candidates.Where(game =>
            game.Versions.Any(version => MatchesVersion(version, fingerprint))).ToArray());
    }

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
               Equal(version.MetadataSha256, fingerprint.MetadataSha256);
    }

    internal static bool MatchesInstalledBuild(GameModuleBuildMatch build, GameVersionProfile version)
    {
        if (new[] { build.BuildFingerprint, build.ExecutableSha256, build.GameAssemblySha256, build.MetadataSha256 }
            .All(string.IsNullOrWhiteSpace)) return false;
        var fingerprint = version.BuildFingerprint;
        if (string.IsNullOrWhiteSpace(fingerprint) && !string.IsNullOrWhiteSpace(version.ExecutableSha256))
            fingerprint = VersionFingerprintService.CreateBuildFingerprint(version.ExecutableSha256,
                version.GameAssemblySha256, version.MetadataSha256);
        return Optional(build.BuildFingerprint, fingerprint) && Optional(build.ExecutableSha256, version.ExecutableSha256) &&
               Optional(build.GameAssemblySha256, version.GameAssemblySha256) && Optional(build.MetadataSha256, version.MetadataSha256);
    }

    internal static bool PathsEqual(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try { return Equal(Path.GetFullPath(left), Path.GetFullPath(right)); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { return false; }
    }

    private static GameProfile? Unique(GameProfile[] games) => games.Length == 1 ? games[0] : null;
    private static bool Equal(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static bool Optional(string expected, string actual) => string.IsNullOrWhiteSpace(expected) || Equal(expected, actual);
}
