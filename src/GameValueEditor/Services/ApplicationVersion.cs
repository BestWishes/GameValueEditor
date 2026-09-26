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

public readonly record struct SemanticVersion(int Major, int Minor, int Patch, string PreRelease)
    : IComparable<SemanticVersion>
{
    public static bool TryParse(string? text, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = text.Trim().TrimStart('v', 'V');
        var metadata = value.IndexOf('+');
        if (metadata >= 0) value = value[..metadata];
        var dash = value.IndexOf('-');
        var core = dash >= 0 ? value[..dash] : value;
        var preRelease = dash >= 0 ? value[(dash + 1)..] : string.Empty;
        var parts = core.Split('.');
        var patch = 0;
        if (parts.Length < 2 || parts.Length > 3 ||
            !int.TryParse(parts[0], out var major) ||
            !int.TryParse(parts[1], out var minor) ||
            (parts.Length == 3 && !int.TryParse(parts[2], out patch))) return false;
        version = new SemanticVersion(major, minor, patch, preRelease);
        return true;
    }

    public int CompareTo(SemanticVersion other)
    {
        var result = Major.CompareTo(other.Major);
        if (result != 0) return result;
        result = Minor.CompareTo(other.Minor);
        if (result != 0) return result;
        result = Patch.CompareTo(other.Patch);
        if (result != 0) return result;
        if (string.IsNullOrEmpty(PreRelease)) return string.IsNullOrEmpty(other.PreRelease) ? 0 : 1;
        if (string.IsNullOrEmpty(other.PreRelease)) return -1;
        return CompareIdentifiers(PreRelease, other.PreRelease);
    }

    private static int CompareIdentifiers(string left, string right)
    {
        var leftParts = left.Split('.');
        var rightParts = right.Split('.');
        for (var index = 0; index < Math.Max(leftParts.Length, rightParts.Length); index++)
        {
            if (index >= leftParts.Length) return -1;
            if (index >= rightParts.Length) return 1;
            var leftNumeric = int.TryParse(leftParts[index], out var leftNumber);
            var rightNumeric = int.TryParse(rightParts[index], out var rightNumber);
            int result;
            if (leftNumeric && rightNumeric) result = leftNumber.CompareTo(rightNumber);
            else if (leftNumeric) result = -1;
            else if (rightNumeric) result = 1;
            else result = string.Compare(leftParts[index], rightParts[index], StringComparison.OrdinalIgnoreCase);
            if (result != 0) return result;
        }
        return 0;
    }
}
