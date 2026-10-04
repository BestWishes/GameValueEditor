using System.Reflection;

namespace GameValueEditor.Services;

public static class ApplicationVersion
{
    public static string Current { get; } = ResolveCurrent();

    private static string ResolveCurrent()
    {
        var value = Assembly.GetEntryAssembly()?
                        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                        .InformationalVersion
                    ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3)
                    ?? "0.0.0";
        var metadata = value.IndexOf('+');
        return (metadata >= 0 ? value[..metadata] : value).TrimStart('v', 'V');
    }
}

public readonly record struct SemanticVersion(int Major, int Minor, int Patch)
    : IComparable<SemanticVersion>
{
    public static bool TryParse(string? text, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = text.Trim().TrimStart('v', 'V');
        var metadata = value.IndexOf('+');
        if (metadata >= 0) value = value[..metadata];
        var parts = value.Split('.');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], out var major) ||
            !int.TryParse(parts[1], out var minor) ||
            !int.TryParse(parts[2], out var patch) ||
            major < 0 || minor < 0 || patch < 0) return false;
        version = new SemanticVersion(major, minor, patch);
        return true;
    }

    public int CompareTo(SemanticVersion other)
    {
        var result = Major.CompareTo(other.Major);
        if (result != 0) return result;
        result = Minor.CompareTo(other.Minor);
        if (result != 0) return result;
        return Patch.CompareTo(other.Patch);
    }
}
