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
        var parts = value.Split('.');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], out var major) ||
            !int.TryParse(parts[1], out var minor) ||
            !int.TryParse(parts[2], out var patch) ||
            major < 0 || minor is < 0 or > 9 || patch is < 0 or > 9 ||
            parts[0] != major.ToString() || parts[1] != minor.ToString() || parts[2] != patch.ToString()) return false;
        version = new SemanticVersion(major, minor, patch);
        return true;
    }

    public SemanticVersion Next() => Patch < 9
        ? this with { Patch = Patch + 1 }
        : Minor < 9
            ? new SemanticVersion(Major, Minor + 1, 0)
            : new SemanticVersion(checked(Major + 1), 0, 0);

    public int CompareTo(SemanticVersion other)
    {
        var result = Major.CompareTo(other.Major);
        if (result != 0) return result;
        result = Minor.CompareTo(other.Minor);
        if (result != 0) return result;
        return Patch.CompareTo(other.Patch);
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}
